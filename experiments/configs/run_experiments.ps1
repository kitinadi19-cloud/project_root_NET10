# Chạy toàn bộ thí nghiệm: mỗi mức Client x 3 lần, restart server trước mỗi lần chạy.
# Dùng (từ thư mục gốc dự án):  powershell -File experiments/configs/run_experiments.ps1
param(
    [int[]]$Levels   = @(1, 10, 50),
    [int]$Repeats    = 3,
    [int]$Port       = 5000,
    [int]$Payload    = 1024,
    [int]$WarmupSec  = 5,
    [int]$DurationSec = 20,
    [string]$Server  = "src/server/bin/Release/net8.0/TcpBench.Server.exe",
    [string]$Gen     = "src/load-generator/bin/Release/net8.0/TcpBench.LoadGen.exe",
    [string]$OutDir  = "experiments/raw-data"
)

foreach ($c in $Levels) {
    foreach ($r in 1..$Repeats) {
        $id  = "c${c}_r${r}"
        $srv = Start-Process $Server -ArgumentList "--port $Port" -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 1

        & $Gen --port $Port --clients $c --payload-bytes $Payload `
               --warmup-s $WarmupSec --duration-s $DurationSec --run-id $id `
               --out "$OutDir/summary.csv" --raw-out "$OutDir/raw_$id.csv" `
               --server-pid $srv.Id

        Stop-Process -Id $srv.Id
        Start-Sleep -Seconds 3          # để hệ thống ổn định giữa các lần chạy
    }
}
