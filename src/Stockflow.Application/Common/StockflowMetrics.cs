using System.Diagnostics.Metrics;

namespace Stockflow.Application.Common;

/// <summary>Business metrics (exported by OpenTelemetry in Api). Built on the BCL Metrics API — no vendor dependency here.</summary>
public sealed class StockflowMetrics
{
    public const string MeterName = "Stockflow";

    private readonly Counter<long> _ordersPlaced;
    private readonly Counter<long> _outOfStock;
    private readonly Counter<long> _ordersPaid;
    private readonly Counter<long> _ordersExpired;
    private readonly Counter<double> _revenue;
    private readonly Counter<long> _webhooks;

    public StockflowMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _ordersPlaced = meter.CreateCounter<long>("stockflow.orders.placed", unit: "{order}");
        _outOfStock = meter.CreateCounter<long>("stockflow.checkout.out_of_stock", unit: "{checkout}",
            description: "Checkouts refused because stock ran out — high values during a sale are expected");
        _ordersPaid = meter.CreateCounter<long>("stockflow.orders.paid", unit: "{order}");
        _ordersExpired = meter.CreateCounter<long>("stockflow.orders.expired", unit: "{order}");
        _revenue = meter.CreateCounter<double>("stockflow.revenue", unit: "RUB");
        _webhooks = meter.CreateCounter<long>("stockflow.webhooks", unit: "{event}");
    }

    public void OrderPlaced() => _ordersPlaced.Add(1);

    public void OutOfStock() => _outOfStock.Add(1);

    public void OrderPaid(decimal amount)
    {
        _ordersPaid.Add(1);
        _revenue.Add((double)amount);
    }

    public void OrdersExpired(int count) => _ordersExpired.Add(count);

    public void Webhook(string outcome) => _webhooks.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
