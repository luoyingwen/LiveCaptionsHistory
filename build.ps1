$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'LiveCaptionsHistory\build.ps1')
exit $LASTEXITCODE
