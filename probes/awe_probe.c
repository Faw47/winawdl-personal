/* SPDX-License-Identifier: MIT */
/* Measures whether a user-mode process can get locked physical memory that a
 * 32-bit-DMA device could use. Allocates, reports, then frees everything. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef MEM_EXTENDED_PARAMETER_NONPAGED_LARGE
#define MEM_EXTENDED_PARAMETER_NONPAGED_LARGE 0x00000008
#endif

#define PAGE_BYTES 4096ull
#define LARGE_BLOCK_BYTES (2ull << 20)
#define PAGES_PER_LARGE_BLOCK (LARGE_BLOCK_BYTES / PAGE_BYTES)
#define FRAME_AT_4GIB (1ull << 20)
#define FRAME_AT_64GIB (1ull << 24)
#define SMALL_PAGE_SAMPLE_MIB 512ull
/* 16 KiB, the radio's largest DMA object, and how many such pieces its DMA
 * area would take if built from ordinary pages. */
#define PIECE_PAGES 4
#define PIECES_NEEDED 768
#define LARGE_BLOCKS_REQUESTED 4096ull
#define LOWEST_BLOCKS_SHOWN 8

typedef BOOL(WINAPI *AllocatePages2Fn)(HANDLE, PULONG_PTR, PULONG_PTR,
                                       PMEM_EXTENDED_PARAMETER, ULONG);
typedef PVOID(WINAPI *VirtualAlloc2Fn)(HANDLE, PVOID, SIZE_T, ULONG, ULONG,
                                       MEM_EXTENDED_PARAMETER *, ULONG);

static FARPROC kernelbase_function(const char *name) {
  return GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), name);
}

static int compare_frames(const void *a, const void *b) {
  ULONG_PTR x = *(const ULONG_PTR *)a, y = *(const ULONG_PTR *)b;
  return (x > y) - (x < y);
}

static BOOL enable_lock_memory_privilege(void) {
  HANDLE token;
  if (!OpenProcessToken(GetCurrentProcess(),
                        TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
    return FALSE;

  TOKEN_PRIVILEGES privileges = {.PrivilegeCount = 1};
  privileges.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
  BOOL found = LookupPrivilegeValueW(NULL, L"SeLockMemoryPrivilege",
                                     &privileges.Privileges[0].Luid);
  if (found) AdjustTokenPrivileges(token, FALSE, &privileges, 0, NULL, NULL);

  /* AdjustTokenPrivileges succeeds even when the privilege is not held. */
  BOOL enabled = found && GetLastError() == ERROR_SUCCESS;
  CloseHandle(token);
  return enabled;
}

/* Ordinary locked pages: how fragmented are they, and how many are low? */
static void probe_small_pages(void) {
  ULONG_PTR count = (ULONG_PTR)(SMALL_PAGE_SAMPLE_MIB * 1024 * 1024 / PAGE_BYTES);
  ULONG_PTR *frames = malloc(count * sizeof(*frames));
  if (!frames) return;

  if (!AllocateUserPhysicalPages(GetCurrentProcess(), &count, frames)) {
    printf("4 KiB pages: allocation failed (%lu)\n", GetLastError());
    free(frames);
    return;
  }

  /* Freeing needs the frames in their original order, so sort a copy. */
  ULONG_PTR *sorted = malloc(count * sizeof(*sorted));
  if (sorted) {
    memcpy(sorted, frames, count * sizeof(*sorted));
    qsort(sorted, count, sizeof(*sorted), compare_frames);

    ULONG_PTR below_4gib = 0, longest_run = 1, run = 1;
    ULONG_PTR piece = 0, pieces = 0, low_pieces = 0;
    for (ULONG_PTR i = 0; i < count; i++) {
      const BOOL adjacent = i > 0 && sorted[i] == sorted[i - 1] + 1;
      if (sorted[i] < FRAME_AT_4GIB) below_4gib++;
      run = adjacent ? run + 1 : 1;
      if (run > longest_run) longest_run = run;
      /* Pieces must not share pages, so the count restarts after each. */
      piece = adjacent ? piece + 1 : 1;
      if (piece == PIECE_PAGES) {
        pieces++;
        if (sorted[i] < FRAME_AT_4GIB) low_pieces++;
        piece = 0;
      }
    }
    printf("4 KiB pages: %llu obtained, %llu below 4 GiB, longest contiguous "
           "run %llu pages\n",
           (unsigned long long)count, (unsigned long long)below_4gib,
           (unsigned long long)longest_run);
    printf("  separate runs of %d pages: %llu, of which %llu below 4 GiB "
           "(the radio would need %d)\n",
           PIECE_PAGES, (unsigned long long)pieces,
           (unsigned long long)low_pieces, PIECES_NEEDED);
    free(sorted);
  }

  FreeUserPhysicalPages(GetCurrentProcess(), &count, frames);
  free(frames);
}

/* The ways an AWE view for a 2 MiB block might have to be reserved. Microsoft
 * documents the allocation but not the mapping, so each is tried. */
typedef enum {
  RESERVE_PLAIN,
  RESERVE_LARGE_ATTRIBUTE,
  RESERVE_LARGE_FLAG,
  RESERVE_KIND_COUNT
} ReserveKind;

static const char *const kReserveNames[RESERVE_KIND_COUNT] = {
    "plain MEM_PHYSICAL", "MEM_PHYSICAL + NONPAGED_LARGE attribute",
    "MEM_PHYSICAL | MEM_LARGE_PAGES"};

static void *reserve_view(VirtualAlloc2Fn virtual_alloc2, ReserveKind kind) {
  MEM_ADDRESS_REQUIREMENTS aligned = {.Alignment = LARGE_BLOCK_BYTES};
  MEM_EXTENDED_PARAMETER params[2] = {0};
  params[0].Type = MemExtendedParameterAddressRequirements;
  params[0].Pointer = &aligned;
  ULONG param_count = 1, type = MEM_RESERVE | MEM_PHYSICAL;

  if (kind == RESERVE_LARGE_ATTRIBUTE) {
    params[1].Type = MemExtendedParameterAttributeFlags;
    params[1].ULong64 = MEM_EXTENDED_PARAMETER_NONPAGED_LARGE;
    param_count = 2;
  } else if (kind == RESERVE_LARGE_FLAG) {
    type |= MEM_LARGE_PAGES;
  }
  return virtual_alloc2(GetCurrentProcess(), NULL, LARGE_BLOCK_BYTES, type,
                        PAGE_READWRITE, params, param_count);
}

/* A successful map call may cover less than the block, so a touch can fault. */
static const char *touch(volatile unsigned char *byte) {
  __try {
    *byte = 0xA5;
    return *byte == 0xA5 ? "ok" : "wrong value";
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return "faults";
  }
}

static void try_mapping(void *view, ULONG_PTR *frames, ULONG_PTR frame_count,
                        const char *reserve_name) {
  printf("  %-42s %3llu frame(s): ", reserve_name,
         (unsigned long long)frame_count);
  fflush(stdout);
  if (!MapUserPhysicalPages(view, frame_count, frames)) {
    printf("map failed (%lu)\n", GetLastError());
    return;
  }
  volatile unsigned char *bytes = view;
  printf("mapped, first byte %s, ", touch(&bytes[0]));
  printf("last byte %s\n", touch(&bytes[LARGE_BLOCK_BYTES - 1]));
  MapUserPhysicalPages(view, frame_count, NULL);
}

static void probe_block_mapping(ULONG_PTR block_frame) {
  VirtualAlloc2Fn virtual_alloc2 =
      (VirtualAlloc2Fn)kernelbase_function("VirtualAlloc2");
  if (!virtual_alloc2) {
    printf("mapping: VirtualAlloc2 is not available\n");
    return;
  }

  /* One entry may stand for the whole block, or one per 4 KiB page. */
  ULONG_PTR whole_block = block_frame;
  ULONG_PTR every_page[PAGES_PER_LARGE_BLOCK];
  for (ULONG_PTR i = 0; i < PAGES_PER_LARGE_BLOCK; i++)
    every_page[i] = block_frame + i;

  printf("mapping one 2 MiB block:\n");
  for (ReserveKind kind = 0; kind < RESERVE_KIND_COUNT; kind++) {
    void *view = reserve_view(virtual_alloc2, kind);
    if (!view) {
      printf("  %-42s reserve failed (%lu)\n", kReserveNames[kind],
             GetLastError());
      continue;
    }
    try_mapping(view, &whole_block, 1, kReserveNames[kind]);
    try_mapping(view, every_page, PAGES_PER_LARGE_BLOCK, kReserveNames[kind]);
    VirtualFree(view, 0, MEM_RELEASE);
  }
}

/* With NONPAGED_LARGE, counts are in 2 MiB blocks and each entry is the frame
 * number of a block's first 4 KiB page. */
static void probe_large_blocks(void) {
  AllocatePages2Fn allocate =
      (AllocatePages2Fn)kernelbase_function("AllocateUserPhysicalPages2");
  if (!allocate) {
    printf("2 MiB blocks: AllocateUserPhysicalPages2 is not available\n");
    return;
  }

  ULONG_PTR count = LARGE_BLOCKS_REQUESTED;
  ULONG_PTR *frames = calloc(count, sizeof(*frames));
  if (!frames) return;

  MEM_EXTENDED_PARAMETER large = {.Type = MemExtendedParameterAttributeFlags};
  large.ULong64 = MEM_EXTENDED_PARAMETER_NONPAGED_LARGE;
  if (!allocate(GetCurrentProcess(), &count, frames, &large, 1)) {
    printf("2 MiB blocks: allocation failed (%lu)\n", GetLastError());
    free(frames);
    return;
  }

  ULONG_PTR below_4gib = 0, below_64gib = 0;
  for (ULONG_PTR i = 0; i < count; i++) {
    ULONG_PTR end = frames[i] + PAGES_PER_LARGE_BLOCK;
    if (end <= FRAME_AT_4GIB) below_4gib++;
    if (end <= FRAME_AT_64GIB) below_64gib++;
  }
  printf("2 MiB blocks: %llu obtained, %llu below 4 GiB, %llu below 64 GiB\n",
         (unsigned long long)count, (unsigned long long)below_4gib,
         (unsigned long long)below_64gib);

  if (count > 0) probe_block_mapping(frames[0]);

  ULONG_PTR *sorted = malloc(count * sizeof(*sorted));
  if (sorted) {
    memcpy(sorted, frames, count * sizeof(*sorted));
    qsort(sorted, count, sizeof(*sorted), compare_frames);
    printf("lowest block addresses:");
    for (ULONG_PTR i = 0; i < count && i < LOWEST_BLOCKS_SHOWN; i++)
      printf(" 0x%llx", (unsigned long long)sorted[i] * PAGE_BYTES);
    printf("\n");
    free(sorted);
  }

  FreeUserPhysicalPages(GetCurrentProcess(), &count, frames);
  free(frames);
}

int main(void) {
  if (!enable_lock_memory_privilege()) {
    printf("SeLockMemoryPrivilege is not available. Run from an elevated "
           "prompt.\n");
    return 1;
  }
  probe_small_pages();
  probe_large_blocks();
  return 0;
}
