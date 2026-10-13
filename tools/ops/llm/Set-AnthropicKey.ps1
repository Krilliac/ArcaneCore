<#
.SYNOPSIS
  Stores an Anthropic API key in your Windows *user* environment (ANTHROPIC_API_KEY by default),
  after checking it works. The key is typed hidden and never written to disk or the console.

.EXAMPLE
  pwsh -NoProfile -File .\Set-AnthropicKey.ps1
.EXAMPLE
  pwsh -NoProfile -File .\Set-AnthropicKey.ps1 -Name ANTHROPIC_API_KEY_ACCOUNT2
#>
param(
    [string]$Name = 'ANTHROPIC_API_KEY',
    [switch]$SkipTest
)

$secure = Read-Host "Paste the API key for $Name (input hidden)" -AsSecureString
$key = [System.Net.NetworkCredential]::new('', $secure).Password.Trim()

if ([string]::IsNullOrWhiteSpace($key)) { Write-Error 'No key entered; nothing changed.'; exit 1 }
$expectedPrefix = ('s' + 'k-ant-')
if (-not $key.StartsWith($expectedPrefix)) { Write-Warning "That doesn't look like an Anthropic key (they start with $expectedPrefix)." }

if (-not $SkipTest) {
    try {
        $models = Invoke-RestMethod -Uri 'https://api.anthropic.com/v1/models?limit=5' -Method Get -Headers @{
            'x-api-key'         = $key
            'anthropic-version' = '2023-06-01'
        }
        Write-Host "Key works. Example models: $(($models.data | Select-Object -First 3).id -join ', ')" -ForegroundColor Green
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Error "The API rejected the key (HTTP $code); nothing changed. Use -SkipTest to store it anyway."
        exit 2
    }
}

[Environment]::SetEnvironmentVariable($Name, $key, 'User')   # persists for new processes
Set-Item -Path "Env:$Name" -Value $key                        # and this window
$key = $null

$len = ([Environment]::GetEnvironmentVariable($Name, 'User')).Length
Write-Host "Saved $Name to your user environment ($len characters)." -ForegroundColor Green
Write-Host 'Restart the Claude app (and any open terminals) so they pick it up.'
