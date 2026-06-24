#Requires -RunAsAdministrator
# Fixes the "mscorlib.XmlSerializers" error from VolumetricaBridge
# Run this ONCE as Admin after install

$ngen = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
if (Test-Path $ngen) {
    Write-Host "Generating XML serializer native images..."
    & $ngen install "mscorlib.XmlSerializers, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" 2>&1
    & $ngen update 2>&1
    Write-Host "Done. Bridge should no longer show 'system file not specified' error."
} else {
    Write-Host "ngen.exe not found at $ngen"
}
