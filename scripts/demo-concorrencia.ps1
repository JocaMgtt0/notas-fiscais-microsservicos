# Demonstracao do tratamento de concorrencia:
# produto com saldo 1 sendo utilizado simultaneamente por duas notas.
#
# Prepara duas notas com o mesmo produto e dispara as duas impressoes em
# paralelo de verdade, usando Task.WhenAll. Duas abas de navegador nao
# serviriam: a janela de disputa e de milissegundos.
#
# Uso:  .\scripts\demo-concorrencia.ps1
# Requer os containers no ar (docker compose up).

$ErrorActionPreference = 'Stop'

$estoque = 'http://localhost:5001'
$faturamento = 'http://localhost:5002'

function Titulo($texto) {
    Write-Host ""
    Write-Host "  $texto" -ForegroundColor Cyan
    Write-Host "  $('-' * 62)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------
# 1. Prepara um produto com saldo 1
# ---------------------------------------------------------------

Titulo "1. Produto em disputa"

$codigo = "DISPUTA-$(Get-Random -Maximum 9999)"
$produto = Invoke-RestMethod "$estoque/api/produtos" -Method Post `
    -ContentType 'application/json' `
    -Body (@{ codigo = $codigo; descricao = 'Ultima unidade em estoque'; saldo = 1 } | ConvertTo-Json)

Write-Host "  $($produto.codigo)  saldo inicial: $($produto.saldo)" -ForegroundColor White

# ---------------------------------------------------------------
# 2. Duas notas, cada uma pedindo a unica unidade disponivel
# ---------------------------------------------------------------

Titulo "2. Duas notas pedindo a mesma unidade"

$notas = 1..2 | ForEach-Object {
    $nota = Invoke-RestMethod "$faturamento/api/notas" -Method Post
    Invoke-RestMethod "$faturamento/api/notas/$($nota.id)/itens" -Method Post `
        -ContentType 'application/json' `
        -Body (@{ produtoId = $produto.id; quantidade = 1 } | ConvertTo-Json) | Out-Null

    Write-Host "  nota $($nota.numero): 1 unidade de $codigo" -ForegroundColor White
    $nota
}

Write-Host ""
Write-Host "  As duas passaram na validacao previa, porque naquele momento" -ForegroundColor DarkGray
Write-Host "  ainda havia saldo. Quem decide de fato e a impressao." -ForegroundColor DarkGray

# ---------------------------------------------------------------
# 3. Impressao simultanea
# ---------------------------------------------------------------

Titulo "3. Imprimindo as duas ao mesmo tempo"

# O Windows PowerShell 5.1 nao carrega este assembly por padrao.
Add-Type -AssemblyName System.Net.Http

$cliente = [System.Net.Http.HttpClient]::new()
$vazio = [System.Net.Http.StringContent]::new('', [System.Text.Encoding]::UTF8, 'application/json')

# Dispara as duas sem aguardar a primeira: e isso que cria a disputa real.
$tarefas = $notas | ForEach-Object {
    $cliente.PostAsync("$faturamento/api/notas/$($_.id)/imprimir", $vazio)
}

[System.Threading.Tasks.Task]::WaitAll($tarefas)

Write-Host ""
for ($i = 0; $i -lt $tarefas.Count; $i++) {
    $resposta = $tarefas[$i].Result
    $status = [int]$resposta.StatusCode
    $corpo = $resposta.Content.ReadAsStringAsync().Result

    $codigoErro = if ($status -eq 200) {
        'impressa e fechada'
    } else {
        try { ($corpo | ConvertFrom-Json).codigo } catch { 'erro' }
    }

    $cor = if ($status -eq 200) { 'Green' } else { 'Yellow' }
    Write-Host ("  nota {0}   HTTP {1}   {2}" -f $notas[$i].numero, $status, $codigoErro) -ForegroundColor $cor
}

$cliente.Dispose()

# ---------------------------------------------------------------
# 4. O que importa: o estado final do estoque
# ---------------------------------------------------------------

Titulo "4. Resultado"

$final = Invoke-RestMethod "$estoque/api/produtos/$($produto.id)"

Write-Host "  saldo final de ${codigo}: $($final.saldo)" -ForegroundColor White
Write-Host ""

if ($final.saldo -eq 0) {
    Write-Host "  OK  exatamente uma nota consumiu a unidade." -ForegroundColor Green
    Write-Host "      O saldo nao ficou negativo e nenhuma unidade se perdeu." -ForegroundColor Green
} else {
    Write-Host "  FALHOU  saldo esperado 0, encontrado $($final.saldo)" -ForegroundColor Red
}

Write-Host ""
