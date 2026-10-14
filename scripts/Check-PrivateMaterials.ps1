$ErrorActionPreference = 'Stop'
$tracked = @(git -c core.quotePath=false ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Nie można sprawdzić listy plików Git.' }
if ($tracked | Where-Object { $_ -match '(^|/)(inspiracje|out z produkcji)/' }) {
    throw 'Materiały prywatne są śledzone przez Git. Przerwano przygotowanie paczki.'
}
Write-Output 'PASS: inspiracje i materiały z produkcji nie są śledzone przez Git.'
if ($tracked | Where-Object { $_ -match '^\.audit/.*\.(dll|exe|pdb)$' }) {
    throw 'W .audit znaleziono śledzone binaria publikacji. Wyniki budowania zapisuj w artifacts/.'
}
Write-Output 'PASS: .audit nie zawiera śledzonego EXE/runtime.'
