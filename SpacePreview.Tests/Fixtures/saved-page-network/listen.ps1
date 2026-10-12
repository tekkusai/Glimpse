# Listens where page.html reaches out and prints whatever arrives.
# Loopback only, so nothing leaves this PC and no firewall prompt appears.
# Stop it with Ctrl+C.

$port = 47811
$ways = [ordered]@{
    '127.0.0.2' = 'image in the page'
    '127.0.0.3' = 'image in a sandboxed srcdoc frame'
    '127.0.0.4' = 'image in a sandboxed data: frame'
    '127.0.0.5' = 'frame from a server'
    '127.0.0.6' = 'image in a sandboxed local frame'
    '127.0.0.7' = 'preconnect'
}

$listeners = foreach ($address in $ways.Keys) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($address), $port)
    $listener.Start()
    $listener
}

Write-Host "Listening on 127.0.0.2 to 127.0.0.7, port $port. Press Space on page.html now."
$arrived = 0
try {
    while ($true) {
        foreach ($listener in $listeners) {
            while ($listener.Pending()) {
                $client = $listener.AcceptTcpClient()
                $address = $client.Client.LocalEndPoint.Address.ToString()
                $stream = $client.GetStream()
                $stream.ReadTimeout = 500
                $buffer = New-Object byte[] 2048
                $line = '(connection, no request)'
                try {
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    if ($read -gt 0) {
                        $line = ([System.Text.Encoding]::ASCII.GetString($buffer, 0, $read) -split "`r`n")[0]
                        $reply = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                        $stream.Write($reply, 0, $reply.Length)
                    }
                }
                catch {
                    # A socket that sends nothing is a preconnect.
                }

                $client.Close()
                $arrived++
                Write-Host ("{0:HH:mm:ss.fff}  {1}  {2}  ({3})" -f (Get-Date), $address, $line, $ways[$address])
            }
        }

        Start-Sleep -Milliseconds 50
    }
}
finally {
    foreach ($listener in $listeners) { $listener.Stop() }
    Write-Host "$arrived arrived."
}
