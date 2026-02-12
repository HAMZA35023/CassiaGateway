namespace AccessAPP.Models
{
    public class ConnectedDevicesView
    {
        public List<Node> nodes { get; set; } = new List<Node>();
    }

    public class Node
    {
        public string id { get; set; }
        public string type { get; set; }
        public Bdaddre bdaddrs { get; set; }
        public int chipId { get; set; } = -1;
        public int chip { get; set; } = -1;
        public string handle { get; set; }
        public string name { get; set; }
        public string connectionState { get; set; }
        public string pairStatus { get; set; }
    }
}
