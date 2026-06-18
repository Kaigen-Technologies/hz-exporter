param([string]$cmd = "ping")

$client = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 7322)
$stream = $client.GetStream()
$bytes = [System.Text.Encoding]::UTF8.GetBytes($cmd)
$stream.Write($bytes, 0, $bytes.Length)
Start-Sleep -Milliseconds 500
$buf = New-Object byte[] 4096
$n = $stream.Read($buf, 0, 4096)
[System.Text.Encoding]::UTF8.GetString($buf, 0, $n)
$client.Close()
