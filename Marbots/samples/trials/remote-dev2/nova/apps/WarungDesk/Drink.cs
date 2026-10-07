namespace WarungDesk;
public record Drink(string Name,string Description,int Price,string Icon) { public string PriceText => $"Rp{Price:N0}"; }