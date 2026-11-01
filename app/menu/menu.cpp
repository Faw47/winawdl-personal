// "Send with AirDrop" in Windows 11's own Explorer menu: the packaged command
// installer\package\AppxManifest.xml declares. Explorer loads this DLL; it only
// starts winawdl.exe with the selected paths, as the classic verbs do.
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <new>
#include <string>
#include <vector>

namespace {

HMODULE g_module;
LONG g_objects;

// {B1BE87C1-E0B3-4C75-92A9-2533736CF7B5}, as in AppxManifest.xml.
constexpr CLSID CLSID_SendWithAirDrop = {
    0xb1be87c1, 0xe0b3, 0x4c75, {0x92, 0xa9, 0x25, 0x33, 0x73, 0x6c, 0xf7, 0xb5}};

constexpr wchar_t kVerbKey[] = L"Software\\Classes\\*\\shell\\winawdl";
constexpr wchar_t kOwnerTag[] = L"winawdl Explorer integration v1";
constexpr size_t kLongestCommandLine = 30000;

std::wstring Folder() {
  std::wstring path(MAX_PATH, L'\0');
  for (;;) {
    DWORD got = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
    if (got == 0) return L"";
    if (got < path.size()) { path.resize(got); break; }
    path.resize(path.size() * 2);
  }
  return path.substr(0, path.find_last_of(L'\\'));
}

bool Windows11() {
  using RtlGetVersion = LONG(WINAPI*)(OSVERSIONINFOW*);
  auto get = reinterpret_cast<RtlGetVersion>(
      reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion")));
  OSVERSIONINFOW version{sizeof(version)};
  return get && get(&version) == 0 && version.dwBuildNumber >= 22000;
}

// Shown where the switch in winawdl's settings is on and the classic verbs are
// hidden for it (WindowsExplorer.cs): never both, never neither.
bool Wanted() {
  if (!Windows11()) return false;
  wchar_t owner[64]{};
  DWORD size = sizeof(owner);
  if (RegGetValueW(HKEY_CURRENT_USER, kVerbKey, L"winawdl-owner", RRF_RT_REG_SZ, nullptr, owner, &size) != ERROR_SUCCESS
      || wcscmp(owner, kOwnerTag) != 0)
    return false;
  return RegGetValueW(HKEY_CURRENT_USER, kVerbKey, L"ProgrammaticAccessOnly", RRF_RT_REG_SZ, nullptr, nullptr,
                      nullptr) == ERROR_SUCCESS;
}

// As CommandLineToArgvW reads it back.
std::wstring Quote(const std::wstring& argument) {
  std::wstring quoted = L"\"";
  size_t slashes = 0;
  for (wchar_t ch : argument) {
    if (ch == L'\\') { ++slashes; continue; }
    quoted.append(ch == L'"' ? slashes * 2 + 1 : slashes, L'\\');
    quoted.push_back(ch);
    slashes = 0;
  }
  quoted.append(slashes * 2, L'\\');
  quoted.push_back(L'"');
  return quoted;
}

void Launch(const std::wstring& app, const std::wstring& arguments) {
  std::wstring line = Quote(app) + arguments;
  STARTUPINFOW startup{sizeof(startup)};
  PROCESS_INFORMATION process{};
  std::wstring folder = app.substr(0, app.find_last_of(L'\\'));
  if (CreateProcessW(app.c_str(), line.data(), nullptr, nullptr, FALSE, 0, nullptr, folder.c_str(), &startup,
                     &process)) {
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
  }
}

class SendCommand final : public IExplorerCommand {
 public:
  SendCommand() { InterlockedIncrement(&g_objects); }

  IFACEMETHODIMP QueryInterface(REFIID riid, void** out) override {
    if (!out) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
      *out = static_cast<IExplorerCommand*>(this);
      AddRef();
      return S_OK;
    }
    *out = nullptr;
    return E_NOINTERFACE;
  }
  IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }
  IFACEMETHODIMP_(ULONG) Release() override {
    LONG left = InterlockedDecrement(&refs_);
    if (left == 0) delete this;
    return left;
  }

  IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(L"Send with AirDrop", name); }
  IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
    return SHStrDupW((Folder() + L"\\winawdl.exe,0").c_str(), icon);
  }
  IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override {
    *tip = nullptr;
    return E_NOTIMPL;
  }
  IFACEMETHODIMP GetCanonicalName(GUID* name) override {
    *name = CLSID_SendWithAirDrop;
    return S_OK;
  }
  IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override {
    *state = Wanted() ? ECS_ENABLED : ECS_HIDDEN;
    return S_OK;
  }
  IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
    *flags = ECF_DEFAULT;
    return S_OK;
  }
  IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override {
    *commands = nullptr;
    return E_NOTIMPL;
  }

  // Each launch joins the running app, which adds to what it holds, so a long
  // selection goes over several command lines.
  IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
    if (!items) return S_OK;
    DWORD count = 0;
    if (FAILED(items->GetCount(&count))) return S_OK;
    const std::wstring app = Folder() + L"\\winawdl.exe";
    std::wstring arguments;
    for (DWORD index = 0; index < count; ++index) {
      IShellItem* item = nullptr;
      if (FAILED(items->GetItemAt(index, &item))) continue;
      PWSTR path = nullptr;
      if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
        std::wstring next = L" " + Quote(path);
        if (!arguments.empty() && arguments.size() + next.size() > kLongestCommandLine) {
          Launch(app, arguments);
          arguments.clear();
        }
        arguments += next;
        CoTaskMemFree(path);
      }
      item->Release();
    }
    if (!arguments.empty()) Launch(app, arguments);
    return S_OK;
  }

 private:
  ~SendCommand() { InterlockedDecrement(&g_objects); }
  LONG refs_ = 1;
};

class Factory final : public IClassFactory {
 public:
  IFACEMETHODIMP QueryInterface(REFIID riid, void** out) override {
    if (!out) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
      *out = static_cast<IClassFactory*>(this);
      return S_OK;
    }
    *out = nullptr;
    return E_NOINTERFACE;
  }
  IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
  IFACEMETHODIMP_(ULONG) Release() override { return 1; }
  IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** out) override {
    if (!out) return E_POINTER;
    *out = nullptr;
    if (outer) return CLASS_E_NOAGGREGATION;
    auto* command = new (std::nothrow) SendCommand();
    if (!command) return E_OUTOFMEMORY;
    HRESULT result = command->QueryInterface(riid, out);
    command->Release();
    return result;
  }
  IFACEMETHODIMP LockServer(BOOL lock) override {
    lock ? InterlockedIncrement(&g_objects) : InterlockedDecrement(&g_objects);
    return S_OK;
  }
};

Factory g_factory;

}  // namespace

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*) {
  if (reason == DLL_PROCESS_ATTACH) {
    g_module = module;
    DisableThreadLibraryCalls(module);
  }
  return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** out) {
  if (!out) return E_POINTER;
  *out = nullptr;
  if (clsid != CLSID_SendWithAirDrop) return CLASS_E_CLASSNOTAVAILABLE;
  return g_factory.QueryInterface(riid, out);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }
