
import argparse
from pathlib import Path

import pandas as pd
import matplotlib.pyplot as plt


BASE_DIR = Path(__file__).resolve().parents[1]
DEFAULT_INPUT = BASE_DIR / "raw-data" / "summary.csv"
DEFAULT_OUTPUT = BASE_DIR / "figures"

METRICS = [
    "mean_ms",
    "p95_ms",
    "throughput_rps",
    "error_rate",
    "srv_cpu_core_pct",
    "srv_cpu_machine_pct",
]

ALIASES = {
    "clients": ["clients", "client_count", "num_clients", "concurrent_clients"],
    "mean_ms": ["mean_ms", "avg_latency_ms", "average_latency_ms"],
    "p95_ms": ["p95_ms", "latency_p95_ms"],
    "throughput_rps": ["throughput_rps", "requests_per_second", "rps"],
    "error_rate": ["error_rate", "error_rate_pct"],
    "srv_cpu_core_pct": ["srv_cpu_core_pct", "cpu_core_pct"],
    "srv_cpu_machine_pct": ["srv_cpu_machine_pct", "cpu_machine_pct"],
}


def find_column(data, possible_names, required=True):
    columns = {column.lower(): column for column in data.columns}

    for name in possible_names:
        if name.lower() in columns:
            return columns[name.lower()]

    if required:
        raise ValueError(
            f"Không tìm thấy cột phù hợp: {', '.join(possible_names)}"
        )

    return None


def load_data(input_path):
    if not input_path.exists():
        raise FileNotFoundError(
            f"Không tìm thấy file dữ liệu: {input_path}\n"
            "Hãy đặt summary.csv vào experiments/raw-data/"
        )

    data = pd.read_csv(input_path)

    if data.empty:
        raise ValueError("File summary.csv không có dữ liệu.")

    client_column = find_column(data, ALIASES["clients"])
    data = data.rename(columns={client_column: "clients"})

    for metric in METRICS:
        column = find_column(data, ALIASES[metric], required=False)

        if column and column != metric:
            data = data.rename(columns={column: metric})

    available_metrics = [metric for metric in METRICS if metric in data.columns]

    if not available_metrics:
        raise ValueError("Không tìm thấy các cột chỉ số cần phân tích.")

    for metric in available_metrics:
        data[metric] = pd.to_numeric(data[metric], errors="coerce")

    data["clients"] = pd.to_numeric(data["clients"], errors="coerce")
    data = data.dropna(subset=["clients"])

    if data.empty:
        raise ValueError("Không có dữ liệu clients hợp lệ.")

    return data, available_metrics


def aggregate_data(data, metrics):
    grouped = data.groupby("clients")[metrics]

    mean = grouped.mean()
    std = grouped.std().fillna(0)
    minimum = grouped.min()
    maximum = grouped.max()

    result = pd.DataFrame(index=mean.index)

    for metric in metrics:
        result[f"{metric}_mean"] = mean[metric]
        result[f"{metric}_std"] = std[metric]
        result[f"{metric}_min"] = minimum[metric]
        result[f"{metric}_max"] = maximum[metric]

    return result.reset_index().sort_values("clients")


def plot_latency(result, output_dir):
    required = ["mean_ms_mean", "p95_ms_mean"]

    if not all(column in result.columns for column in required):
        print("Bỏ qua biểu đồ latency: thiếu dữ liệu mean_ms hoặc p95_ms.")
        return

    x = result["clients"]

    plt.figure(figsize=(8, 5))
    plt.errorbar(
        x,
        result["mean_ms_mean"],
        yerr=result["mean_ms_std"],
        marker="o",
        capsize=5,
        label="Mean Latency",
    )
    plt.plot(
        x,
        result["p95_ms_mean"],
        marker="s",
        linestyle="--",
        label="P95 Latency",
    )

    plt.xlabel("Number of Concurrent Clients")
    plt.ylabel("Latency (ms)")
    plt.title("Latency vs Number of Clients")
    plt.xticks(x)
    plt.grid(True, alpha=0.3)
    plt.legend()
    plt.tight_layout()
    plt.savefig(output_dir / "latency_vs_clients.png", dpi=300)
    plt.close()


def plot_metric(result, metric, ylabel, title, filename, output_dir):
    mean_column = f"{metric}_mean"
    std_column = f"{metric}_std"

    if mean_column not in result.columns:
        print(f"Bỏ qua biểu đồ {filename}: thiếu dữ liệu {metric}.")
        return

    plt.figure(figsize=(8, 5))
    plt.errorbar(
        result["clients"],
        result[mean_column],
        yerr=result[std_column],
        marker="o",
        capsize=5,
    )

    plt.xlabel("Number of Concurrent Clients")
    plt.ylabel(ylabel)
    plt.title(title)
    plt.xticks(result["clients"])
    plt.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(output_dir / filename, dpi=300)
    plt.close()


def main():
    parser = argparse.ArgumentParser(
        description="Analyze TCP Server experiment results."
    )
    parser.add_argument(
        "--input",
        type=Path,
        default=DEFAULT_INPUT,
        help="Path to summary.csv",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help="Directory for aggregate.csv and charts",
    )

    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    data, metrics = load_data(args.input)
    result = aggregate_data(data, metrics)

    output_csv = args.output / "aggregate.csv"
    result.to_csv(output_csv, index=False)

    plot_latency(result, args.output)

    plot_metric(
        result,
        "throughput_rps",
        "Throughput (requests/second)",
        "Throughput vs Number of Clients",
        "throughput_vs_clients.png",
        args.output,
    )

    
    cpu_metric = (
        "srv_cpu_core_pct"
         if "srv_cpu_core_pct" in metrics
         else "srv_cpu_machine_pct"
    )


    plot_metric(
        result,
        cpu_metric,
        "Server CPU Usage (% of one core)",
        "Server CPU Usage vs Number of Clients",
        "cpu_vs_clients.png",
        args.output,
    )

    print("\nPhân tích dữ liệu hoàn tất!")
    print(f"File tổng hợp: {output_csv}")
    print(f"Thư mục biểu đồ: {args.output}")
    print("\nBảng kết quả:")
    print(result.to_string(index=False))


if __name__ == "__main__":
    main()
