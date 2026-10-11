# Starts the AirDrop receiver on the AWDL adapter. Run scripts\awdl-run.ps1 in
# another prompt first; this one needs no elevation. Arguments are passed on,
# for example: --name "Study PC" --outdir D:\Drops
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$python = Get-Command py, python -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python 3.9 or newer is required.' }

& $python.Source "$repo\airdrop\receiver.py" @args
