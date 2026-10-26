# ArcaneCore remote-admin (RA) client for PowerShell 5+ (docs/ops/remote-admin.md, docs/ops/botctl.md).
#   . tools/botctl/RaClient.ps1
#   $ra = Connect-Ra -TokenFile ra-token.txt     # or -User ADMIN -Password secret
#   $r = Invoke-Ra $ra '.botctl state Alice --json'; $r.Ok; $r.Reason; $r.Json.bots[0].data.hp
#   Disconnect-Ra $ra
# Invoke-Ra returns [pscustomobject]@{ Command; Ok; Reason; Lines; Json; Events }.

function Connect-Ra {
    param(
        [string]$HostName = '127.0.0.1',
        [int]$Port = 3443,
        [string]$Token,
        [string]$TokenFile,
        [string]$User,
        [string]$Password,
        [int]$TimeoutSeconds = 1900
    )
    if (-not $Token -and $TokenFile) { $Token = (Get-Content -Raw -LiteralPath $TokenFile).Trim() }
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($HostName, $Port)
    $client.ReceiveTimeout = $TimeoutSeconds * 1000
    $stream = $client.GetStream()
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $reader = New-Object System.IO.StreamReader($stream, $utf8)
    $writer = New-Object System.IO.StreamWriter($stream, $utf8)
    $writer.NewLine = "`r`n"; $writer.AutoFlush = $true
    $ra = [pscustomobject]@{ Client = $client; Reader = $reader; Writer = $writer; Seq = 0 }
    while ((Read-RaLine $ra) -ne 'Username:') { }
    if ($Token) { $writer.WriteLine("@token $Token") }
    elseif ($User) { $writer.WriteLine("@login $User $Password") }
    else { throw 'Connect-Ra needs -Token, -TokenFile or -User/-Password' }
    $answer = Read-RaLine $ra
    if (-not $answer.StartsWith('+')) { $client.Close(); throw "RA login failed: $answer" }
    return $ra
}

function Read-RaLine($ra) {
    $line = $ra.Reader.ReadLine()
    if ($null -eq $line) { throw 'RA connection closed' }
    while ($line.StartsWith('AC> ')) { $line = $line.Substring(4) }
    return $line
}

function Invoke-Ra {
    param([Parameter(Mandatory)]$Ra, [Parameter(Mandatory)][string]$Command)
    $Ra.Seq++
    $tag = "p$($Ra.Seq)"
    $Ra.Writer.WriteLine("@id $tag $Command")
    $lines = New-Object System.Collections.Generic.List[string]
    $events = New-Object System.Collections.Generic.List[string]
    $prefix = "#done $tag "
    while ($true) {
        $line = Read-RaLine $Ra
        if ($line.StartsWith($prefix)) {
            $status = $line.Substring($prefix.Length)
            $ok = $status -eq 'ok'
            $reason = if ($ok) { $null } elseif ($status.StartsWith('error ')) { $status.Substring(6) } else { $status }
            $json = $null
            foreach ($l in $lines) { if ($l.TrimStart().StartsWith('{')) { try { $json = $l | ConvertFrom-Json } catch { } ; break } }
            return [pscustomobject]@{ Command = $Command; Ok = $ok; Reason = $reason; Lines = $lines.ToArray(); Json = $json; Events = $events.ToArray() }
        }
        if ($line.StartsWith('#event')) { $events.Add($line) } else { $lines.Add($line) }
    }
}

function Disconnect-Ra($ra) {
    try { $ra.Writer.WriteLine('quit') } catch { }
    $ra.Client.Close()
}