# TCP Echo Benchmark (C# / .NET 8)

Prototype phục vụ bài nghiên cứu: **Khảo sát ảnh hưởng của số lượng Client đồng thời đến hiệu năng TCP Server** (latency, throughput, CPU).

* **Server (`TcpBench.Server`):** Asynchronous socket (`async/await`), framing theo độ dài (big-endian), bật `NoDelay`.
* **Load Generator (`TcpBench.LoadGen`):** Mô hình tải closed-loop, mỗi Client một kết nối TCP riêng, ghi kết quả tổng hợp và dữ liệu thô dạng CSV.
* **Báo cáo chi tiết:** Xem lý thuyết, kết quả thực nghiệm, phân tích và đóng góp thành viên trong thư mục `docs/`.

\---

## Yêu cầu môi trường

* [.NET 8 SDK](https://dotnet.microsoft.com/)
* Python 3 (dùng thư viện chuẩn cho `verify.py`)
* `matplotlib` (chỉ cần để chạy `analyze.py`):

```bash
  pip install matplotlib
  ```

\---

## Cấu trúc thư mục

```text
├── README.md
├── .gitignore
├── src/
│   ├── server/           # TCP Echo Server (TcpBench.Server)
│   └── load-generator/   # Chương trình tạo tải (TcpBench.LoadGen)
├── tests/                # Bộ kiểm thử tự động xUnit (T1–T8)
├── experiments/
│   ├── configs/          # Script chạy thực nghiệm (run\_experiments.ps1)
│   ├── raw-data/         # Dữ liệu thô (summary.csv, raw\_\*.csv)
│   ├── analysis/         # Script phân tích (analyze.py) \& kiểm chứng (verify.py)
│   └── figures/          # Biểu đồ (.png) và bảng tổng hợp (aggregate.csv)
└── docs/                 # Báo cáo PDF
```

\---

|STT|Họ và tên|Vai trò|
|-|-|-|
|1|Nguyễn Chánh Tư Duy|Nhóm trưởng, quản lý dự án, viết báo cáo|
|2|Nguyễn Thành Đạt|TCP Server|
|3|Phan Trần Quang Lâm|Load generator|
|4|Nguyễn Chí Hiến|Thực nghiệm \& đo lường|
|5|Nguyễn Anh Khôi|Phân tích dữ liệu \& biểu đồ|
|6|Hoàng Thành Đạt|Kiểm thử, kiểm chứng|

*Vai trò chính của nhóm, cụ thể mời xem báo cáo của nhóm*

## Build và chạy kiểm thử

Chạy từ thư mục gốc của dự án:

```bash
dotnet build src/server -c Release
dotnet build src/load-generator -c Release
dotnet test tests
```

\---

## Tái tạo một thí nghiệm (10 Client, 1 KB payload)

Mở 2 cửa sổ terminal riêng biệt:

**Terminal 1 — Khởi chạy Server:**

```powershell
src/server/bin/Release/net8.0/TcpBench.Server.exe --port 5000
```

**Terminal 2 — Khởi chạy Load Generator (warm-up 5s, đo 20s):**

```powershell
src/load-generator/bin/Release/net8.0/TcpBench.LoadGen.exe --port 5000 `
    --clients 10 --payload-bytes 1024 --warmup-s 5 --duration-s 20 `
    --run-id c10\_r1 --out experiments/raw-data/summary.csv `
    --raw-out experiments/raw-data/raw\_c10\_r1.csv --server-name TcpBench.Server
```

*(Trên Linux/macOS: bỏ đuôi `.exe` và dùng `\\` thay cho ```).*

\---

## Chạy toàn bộ thí nghiệm (1 / 10 / 50 Client × 3 lần)

```powershell
powershell -File experiments/configs/run\_experiments.ps1
```

\---

## Phân tích dữ liệu \& Kiểm chứng

```bash
# Tổng hợp số liệu (aggregate.csv) và vẽ 3 biểu đồ vào experiments/figures/
python experiments/analysis/analyze.py

# Kiểm chứng độc lập dữ liệu thô với summary.csv
python experiments/analysis/verify.py experiments/raw-data
```

\---

## Tham số dòng lệnh chính

* **Server:** `--ip` (mặc định `127.0.0.1`), `--port` (`5000`), `--idle-timeout-ms` (`10000`).
* **LoadGen:** `--ip`, `--port`, `--clients`, `--payload-bytes`, `--warmup-s`, `--duration-s`, `--timeout-ms`, `--run-id`, `--out`, `--raw-out`, `--server-pid` | `--server-name`.

