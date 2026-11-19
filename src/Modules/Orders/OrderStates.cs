namespace Delivery.Modules.Orders;
public static class OrderStates
{
 private static readonly Dictionary<string,string[]> Edges=new()
 {
  ["payment_pending"]=["confirmed","cancelled"],
  ["confirmed"]=["accepted","cancelled"],
  ["accepted"]=["preparing","cancelled"],
  ["preparing"]=["ready","cancelled"],
  ["ready"]=["picked_up","cancelled"],
  ["picked_up"]=["in_transit","incident"],
  ["in_transit"]=["delivered","incident"],
  ["incident"]=["in_transit","delivered","returned"],
  ["delivered"]=["closed"],
  ["returned"]=[],
  ["closed"]=[],
  ["cancelled"]=[]
 };
 public static bool Allowed(string from,string to)=>Edges.TryGetValue(from,out var choices)&&choices.Contains(to);
 public static bool Terminal(string state)=>state is "closed" or "cancelled" or "returned";
}

