function Test-RdpService {
    param (
        [Parameter(Mandatory=$true)]
        [string]$ComputerName,
        [int]$Port = 3389,
        [int]$TimeoutMs = 2000
    )

    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $asyncResult = $client.BeginConnect($ComputerName, $Port, $null, $null)
        
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            $client.Close()
            return [PSCustomObject]@{ ComputerName = $ComputerName; RdpResponding = $false; Error = "Connection Timeout" }
        }

        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs

        # X.224 Connection Request PDU asking for standard RDP / TLS negotiation
        [byte[]]$rdpPacket = @(
            0x03, 0x00, 0x00, 0x13,  # TPKT Header (length 19)
            0x0e, 0xe0, 0x00, 0x00, 0x00, 0x00, 0x00,  # X.224 Connection Request
            0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00 # RDP Neg Request (SSL/NLA)
        )

        $stream.Write($rdpPacket, 0, $rdpPacket.Length)

        [byte[]]$response = New-Object byte[] 4
        $bytesRead = $stream.Read($response, 0, $response.Length)
        $client.Close()

        # Check for valid TPKT header response (Starts with 0x03 0x00)
        $isAlive = ($bytesRead -ge 2 -and $response[0] -eq 0x03 -and $response[1] -eq 0x00)

        return [PSCustomObject]@{
            ComputerName  = $ComputerName
            RdpResponding = $isAlive
            Error         = if ($isAlive) { $null } else { "Invalid RDP response" }
        }
    }
    catch {
        return [PSCustomObject]@{ ComputerName = $ComputerName; RdpResponding = $false; Error = $_.Exception.Message }
    }
}

# Example Usage:
Test-RdpService -ComputerName "10.34.9.34"
