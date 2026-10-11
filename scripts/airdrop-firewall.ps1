#Requires -RunAsAdministrator
# Lets AirDrop senders through Windows Firewall: the HTTPS port and multicast
# DNS, from link-local addresses only. -Remove deletes the rules again.
param([switch]$Remove, [int]$Port = 8771)
$ErrorActionPreference = 'Stop'

$Group = 'winawdl AirDrop'
Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($Remove) { 'Firewall rules removed.'; return }

$common = @{
  Group = $Group; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any'
  RemoteAddress = 'fe80::/64'
}
New-NetFirewallRule @common -DisplayName 'winawdl AirDrop receiver' -Protocol TCP -LocalPort $Port |
  Out-Null
New-NetFirewallRule @common -DisplayName 'winawdl AirDrop discovery' -Protocol UDP -LocalPort 5353 |
  Out-Null
Get-NetFirewallRule -Group $Group | Select-Object DisplayName, Enabled, Direction, Action
