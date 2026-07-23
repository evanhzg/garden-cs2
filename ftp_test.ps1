$ftpServer = "ftp://baroque.dathost.net:21"
$ftpUser = "67fd3fd5caae0fdc8408ff64"
$ftpPass = "iyoGJKy0aEQ"

$uri = New-Object System.Uri("$ftpServer/")
$request = [System.Net.FtpWebRequest]::Create($uri)
$request.Credentials = New-Object System.Net.NetworkCredential($ftpUser, $ftpPass)
$request.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory

try {
    $response = $request.GetResponse()
    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
    Write-Host "--- FTP ROOT ---"
    Write-Host $reader.ReadToEnd()
    $reader.Close()
    $response.Close()
} catch {
    Write-Host "Error listing root: $_"
}

$uri2 = New-Object System.Uri("$ftpServer/game/csgo/addons/counterstrikesharp/plugins/")
$request2 = [System.Net.FtpWebRequest]::Create($uri2)
$request2.Credentials = New-Object System.Net.NetworkCredential($ftpUser, $ftpPass)
$request2.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory

try {
    $response2 = $request2.GetResponse()
    $reader2 = New-Object System.IO.StreamReader($response2.GetResponseStream())
    Write-Host "--- PLUGINS (csgo) ---"
    Write-Host $reader2.ReadToEnd()
    $reader2.Close()
    $response2.Close()
} catch {
    Write-Host "Error listing plugins (csgo): $_"
}

$uri3 = New-Object System.Uri("$ftpServer/addons/counterstrikesharp/plugins/")
$request3 = [System.Net.FtpWebRequest]::Create($uri3)
$request3.Credentials = New-Object System.Net.NetworkCredential($ftpUser, $ftpPass)
$request3.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory

try {
    $response3 = $request3.GetResponse()
    $reader3 = New-Object System.IO.StreamReader($response3.GetResponseStream())
    Write-Host "--- PLUGINS (addons) ---"
    Write-Host $reader3.ReadToEnd()
    $reader3.Close()
    $response3.Close()
} catch {
    Write-Host "Error listing plugins (addons): $_"
}
