# Sends files to an iPhone or a Mac over AirDrop; scripts\awdl-run.ps1 must
# already be running, and the receiver set to "Everyone for 10 Minutes".
# Arguments are passed on, for example: photo.jpg clip.mp4 --to "Anna"
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$python = Get-Command py, python -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python 3.10 or newer is required.' }

& $python.Source "$repo\airdrop\sender.py" @args
