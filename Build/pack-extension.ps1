# 打包 Chrome 扩展为 .crx3（Chrome 现在的格式）
# 私钥只在本机使用，不进版本库；首次运行会自动生成 extension.pem
param(
    [string]$ExtensionDir = "$PSScriptRoot\..\extension",
    [string]$OutFile = "$PSScriptRoot\..\Resources\StoreImport.crx",
    [string]$KeyFile = "$PSScriptRoot\extension.pem"
)

$ErrorActionPreference = 'Stop'

function New-RsaKey([string]$pemPath) {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $pem = $rsa.ExportRSAPrivateKeyPem()
    [System.IO.File]::WriteAllText($pemPath, $pem, [System.Text.UTF8Encoding]::new($false))
    Write-Host "已生成私钥: $pemPath（请勿提交到版本库）"
    return $rsa
}

function Get-RsaFromPem([string]$pemPath) {
    $pem = [System.IO.File]::ReadAllText($pemPath)
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($pem)
    return $rsa
}

# 私钥：有就复用，没有就生成。扩展 ID 由公钥决定，换私钥会导致 ID 变化，
# 所以这份 pem 一旦生成就应当长期保留
if (Test-Path $KeyFile) {
    $rsa = Get-RsaFromPem $KeyFile
    Write-Host "复用现有私钥: $KeyFile"
} else {
    $rsa = New-RsaKey $KeyFile
}

# 扩展 ID：公钥 SHA256 前 16 字节，每个半字节映射到 a-p
$pub = $rsa.ExportSubjectPublicKeyInfo()
$sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($pub)
$id = ''
for ($i = 0; $i -lt 16; $i++) {
    $b = $sha[$i]
    $id += [char](97 + ($b -shr 4))
    $id += [char](97 + ($b -band 0x0F))
}
Write-Host "扩展 ID: $id"

# 打 zip：crx3 的 payload 就是扩展目录的 zip
$tempZip = Join-Path $env:TEMP ("crx-" + [guid]::NewGuid().ToString('N') + ".zip")
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
Compress-Archive -Path (Join-Path $ExtensionDir '*') -DestinationPath $tempZip -Force

$payload = [System.IO.File]::ReadAllBytes($tempZip)

# Crx3 结构：magic "Cr24" + version(4) + headerLen(4) + header + payload
# header 里放 sha256_with_rsa 签名块：signature_len + signature + pubkey_len + pubkey
$sig = $rsa.SignData($payload,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

$pubBytes = $rsa.ExportSubjectPublicKeyInfo()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# protobuf: CrxFileHeader { repeated AsymmetricKeyProof sha256_with_rsa = 2; }
# AsymmetricKeyProof { bytes public_key = 1; bytes signature = 2; }
function Write-Varint($writer, [uint64]$value) {
    while ($value -ge 0x80) {
        $writer.Write([byte](($value -band 0x7F) -bor 0x80))
        $value = $value -shr 7
    }
    $writer.Write([byte]$value)
}

function Write-LengthDelimited($writer, [byte[]]$data) {
    Write-Varint $writer ([uint64]$data.Length)
    $writer.Write($data)
}

# AsymmetricKeyProof 序列化
$proof = New-Object System.IO.MemoryStream
$pw = New-Object System.IO.BinaryWriter($proof)
$pw.Write([byte]0x0A); Write-LengthDelimited $pw $pubBytes
$pw.Write([byte]0x12); Write-LengthDelimited $pw $sig
$pw.Flush()
$proofBytes = $proof.ToArray()

# CrxFileHeader：field 2 (sha256_with_rsa)，外层不写 signed_header_data 也合法
$header = New-Object System.IO.MemoryStream
$hw = New-Object System.IO.BinaryWriter($header)
$hw.Write([byte]0x12); Write-LengthDelimited $hw $proofBytes
$hw.Flush()
$headerBytes = $header.ToArray()

# 写 Crx3
$bw.Write([byte[]][char[]]'Cr24')
$bw.Write([uint32]3)
$bw.Write([uint32]$headerBytes.Length)
$bw.Write($headerBytes)
$bw.Write($payload)
$bw.Flush()

$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, $ms.ToArray())

Remove-Item $tempZip -Force -ErrorAction SilentlyContinue

$size = (Get-Item $OutFile).Length
Write-Host "已生成: $OutFile ($([math]::Round($size / 1KB, 1)) KB)"
Write-Host "提示：.crx 需要拖入 chrome://extensions 加载；若被策略拒绝，改用「加载已解压的扩展程序」指向 extension 目录。"
