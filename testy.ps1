$ErrorActionPreference = 'Continue'

function Uruchom {
    param([string]$Opis, [string[]]$Argumenty)
    Write-Output ('info: dotnet ' + ($Argumenty -join ' '))
    & dotnet @Argumenty
    if ($LASTEXITCODE -ne 0) {
        Write-Output ('blad: ' + $Opis + ' zwrocilo ' + $LASTEXITCODE)
        exit $LASTEXITCODE
    }
}

$sln = 'src/MaszynyAlert.sln'
Uruchom 'restore' @('restore', $sln)
Uruchom 'build' @('build', $sln, '-c', 'Release', '--no-restore', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
Uruchom 'test' @('test', $sln, '-c', 'Release', '--no-build', '-m:1', '-nr:false', '--logger', 'trx', '--results-directory', 'TestResults/Windows')
exit 0
