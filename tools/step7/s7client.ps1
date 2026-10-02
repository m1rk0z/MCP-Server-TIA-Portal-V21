# --- Client S7comm minimale: TPKT / COTP / S7, SOLA LETTURA (Read Var; la scrittura non e implementata) ---
param([string]$IP='127.0.0.1',[int]$Rack=0,[int]$Slot=2,[int]$Port=102)

function New-Tpkt([byte[]]$payload){
  $len = $payload.Length + 4
  return ,([byte[]](0x03,0x00,[byte](($len -shr 8) -band 0xFF),[byte]($len -band 0xFF)) + $payload)
}
function Send-Recv($stream,[byte[]]$pkt){
  $stream.Write($pkt,0,$pkt.Length); $stream.Flush()
  $hdr = New-Object byte[] 4; $n=0
  while($n -lt 4){ $r=$stream.Read($hdr,$n,4-$n); if($r -le 0){ throw "connessione chiusa" }; $n+=$r }
  $tot = ($hdr[2] -shl 8) -bor $hdr[3]
  $rest = New-Object byte[] ($tot-4); $n=0
  while($n -lt $rest.Length){ $r=$stream.Read($rest,$n,$rest.Length-$n); if($r -le 0){ throw "connessione chiusa" }; $n+=$r }
  return ,$rest
}
function Connect-S7($ip,$rack,$slot,$port){
  $cli = New-Object System.Net.Sockets.TcpClient
  $cli.SendTimeout=5000; $cli.ReceiveTimeout=5000
  $iar = $cli.BeginConnect($ip,$port,$null,$null)
  if(-not $iar.AsyncWaitHandle.WaitOne(5000)){ $cli.Close(); throw "timeout TCP su ${ip}:${port}" }
  $cli.EndConnect($iar)
  $st = $cli.GetStream(); $st.ReadTimeout=5000; $st.WriteTimeout=5000
  $dst = [byte](($rack -shl 5) -bor $slot)
  $cotp = [byte[]](0x11,0xE0,0x00,0x00,0x00,0x01,0x00, 0xC0,0x01,0x0A, 0xC1,0x02,0x01,0x00, 0xC2,0x02,0x01,$dst)
  $resp = Send-Recv $st (New-Tpkt $cotp)
  if($resp[1] -ne 0xD0){ $cli.Close(); throw ("COTP rifiutato, tipo=0x{0:X2}" -f $resp[1]) }
  # Setup communication
  $s7 = [byte[]](0x32,0x01,0x00,0x00,0x00,0x01,0x00,0x08,0x00,0x00, 0xF0,0x00,0x00,0x01,0x00,0x01,0x03,0xC0)
  $resp = Send-Recv $st (New-Tpkt ([byte[]](0x02,0xF0,0x80) + $s7))
  $pdu = ($resp[21] -shl 8) -bor $resp[22]
  return @{ Client=$cli; Stream=$st; Pdu=$pdu }
}
function Read-DB($conn,[int]$db,[int]$start,[int]$size){
  $a = $start * 8
  $item = [byte[]](0x12,0x0A,0x10,0x02, [byte](($size -shr 8) -band 0xFF),[byte]($size -band 0xFF),
                   [byte](($db -shr 8) -band 0xFF),[byte]($db -band 0xFF), 0x84,
                   [byte](($a -shr 16) -band 0xFF),[byte](($a -shr 8) -band 0xFF),[byte]($a -band 0xFF))
  $par = [byte[]](0x04,0x01) + $item
  $s7  = [byte[]](0x32,0x01,0x00,0x00,0x00,0x02, [byte](($par.Length -shr 8) -band 0xFF),[byte]($par.Length -band 0xFF), 0x00,0x00) + $par
  $resp = Send-Recv $conn.Stream (New-Tpkt ([byte[]](0x02,0xF0,0x80) + $s7))
  # resp: [0..2]=COTP, [3..14]=S7 hdr ack_data(12), poi par(2), poi data item
  $off = 3 + 12 + 2
  if($resp[13] -ne 0 -or $resp[14] -ne 0){ throw ("errore S7 class=0x{0:X2} code=0x{1:X2}" -f $resp[13],$resp[14]) }
  $rc = $resp[$off]
  if($rc -ne 0xFF){ throw ("lettura fallita, return code=0x{0:X2}" -f $rc) }
  $len = (($resp[$off+2] -shl 8) -bor $resp[$off+3]) / 8
  return ,$resp[($off+4)..($off+3+$len)]
}
function Get-Word([byte[]]$b,[int]$i){ return (($b[$i] -shl 8) -bor $b[$i+1]) }
