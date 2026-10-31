# Starts the AirDrop receiver; scripts\awdl-run.ps1 must already be running.
# Arguments are passed on, for example: --name "Study PC" --outdir D:\Drops
# --redact prints no names, addresses or account folders.
# --contacts-only --known-senders FILE accepts only the Apple IDs in FILE.
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$python = Get-Command py, python -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python 3.10 or newer is required.' }

& $python.Source "$repo\airdrop\receiver.py" @args
