using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System;
using System.Linq;
namespace WarungDesk;
public partial class MainWindow : Window {
 readonly List<Drink> menu = new(){new("Es Kopi Senja","Kopi susu gula aren",24000,"◐"),new("Kopi Tubruk","Robusta lokal, pekat",18000,"◒"),new("Cappuccino","Espresso & susu lembut",28000,"◓"),new("Matcha Senja","Matcha, susu, madu",27000,"✳"),new("Teh Melati","Wangi melati pilihan",16000,"❋"),new("Cokelat Hangat","Cokelat Belgia, creamy",26000,"●")};
 public MainWindow(){InitializeComponent();MenuList.ItemsSource=menu;}
 void OnSelectionChanged(object? s, SelectionChangedEventArgs e){var selected=(MenuList.SelectedItems ?? Array.Empty<object>()).Cast<Drink>().ToList();CountText.Text=$"{selected.Count} minuman";TotalText!.Text=$"Rp{selected.Sum(x=>x.Price):N0}";}
 void OnClear(object? s,RoutedEventArgs e){MenuList.SelectedItems.Clear();}
}