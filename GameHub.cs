using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

public class GameHub : Hub
{
    private static readonly object _lock = new object();
    private static readonly string[] Suits = new[] { "♠", "♥", "♦", "♣" };
    private static readonly string[] Ranks = new[] { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    private static GameState State = new GameState();

    public override async Task OnConnectedAsync()
    {
        lock (_lock)
        {
            if (State.HostId == null) State.HostId = Context.ConnectionId;
        }
        await Clients.Caller.SendAsync("Welcome", Context.ConnectionId);
        await Clients.All.SendAsync("State", State.ToDto());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        lock (_lock)
        {
            if (State.ConnectionSeat.TryGetValue(Context.ConnectionId, out var seat))
            {
                State.RemoveSeat(seat);
            }
            if (State.HostId == Context.ConnectionId) State.HostId = null;
        }
        await Clients.All.SendAsync("State", State.ToDto());
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Join(string name)
    {
        if (State.Phase != "BETTING")
        {
            await Clients.Caller.SendAsync("JoinFailed", "Dołączanie wyłączone podczas gry");
            return;
        }
        bool ok;
        int assignedSeat;
        lock (_lock)
        {
            (ok, assignedSeat) = State.AddPlayer(Context.ConnectionId, name);
        }
        if (!ok)
        {
            var dup = false;
            lock(_lock){ dup = State.ConnectionSeat.ContainsKey(Context.ConnectionId); }
            await Clients.Caller.SendAsync("JoinFailed", dup?"Już jesteś w grze":"Brak wolnych miejsc");
            return;
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task Kick(int seat)
    {
        if (!IsHost(Context)) return;
        lock (_lock)
        {
            State.RemoveSeat(seat);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task StartGame()
    {
        if (!IsHost(Context)) return;
        lock (_lock)
        {
            State.StartGame();
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task NewBets()
    {
        if (!IsHost(Context)) return;
        lock (_lock)
        {
            State.NewBets();
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task Hit()
    {
        lock (_lock)
        {
            if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
            State.Hit(seat);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task Stand()
    {
        lock (_lock)
        {
            if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
            State.Stand(seat);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task PlaceBet(int amount)
    {
        lock (_lock)
        {
            if (State.Phase != "BETTING") return;
            if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
            State.PlaceBet(seat, amount);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task DoubleDown()
    {
        lock (_lock)
        {
            if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
            State.DoubleDown(seat);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task Split()
    {
        lock (_lock)
        {
            if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
            State.Split(seat);
        }
        await Clients.All.SendAsync("State", State.ToDto());
    }

    private bool IsHost(HubCallerContext ctx)
    {
        var http = ctx.GetHttpContext();
        var ip = http?.Connection.RemoteIpAddress;
        if (ip != null && (System.Net.IPAddress.IsLoopback(ip))) return true;
        if (State.HostId == ctx.ConnectionId) return true;
        if (State.AdminId == ctx.ConnectionId) return true;
        return false;
    }

    private class Card
    {
        public string r { get; set; } = "";
        public string s { get; set; } = "";
    }

    private class Player
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Card> Hand { get; set; } = new List<Card>();
        public List<Card>? Hand1 { get; set; }
        public List<Card>? Hand2 { get; set; }
        public bool Stood { get; set; }
        public bool Finished { get; set; }
        public int Seat { get; set; }
        public int Money { get; set; }
        public int Bet { get; set; }
        public int Bet1 { get; set; }
        public int Bet2 { get; set; }
        public bool Finished1 { get; set; }
        public bool Finished2 { get; set; }
    }

    private class GameState
    {
        public string? HostId { get; set; }
        public string? AdminId { get; set; }
        public List<Card> Dealer { get; set; } = new List<Card>();
        public List<Player> Players { get; set; } = new List<Player>();
        public Dictionary<string,int> ConnectionSeat { get; set; } = new Dictionary<string,int>();
        public int? ActiveSeat { get; set; }
        public bool Finished { get; set; }
        public string Phase { get; set; } = "BETTING";
        public int DeckCount { get; set; }
        private const int DecksInShoe = 6;
        private List<Card> Deck { get; set; } = new List<Card>();

        public GameState()
        {
            Deck = BuildDeck();
            Shuffle(Deck);
            DeckCount = Deck.Count;
        }

        public (bool,int) AddPlayer(string id, string name)
        {
            if (ConnectionSeat.ContainsKey(id))
            {
                return (false, -1);
            }
            for(int seat=0; seat<5; seat++)
            {
                if (Players.Any(p=>p.Seat==seat)) continue;
                var nm = (name??"").Trim(); if (nm.Length>10) nm = nm.Substring(0,10);
                var p = new Player{ Id=id, Name=nm, Seat=seat, Money=1000 };
                Players.Add(p);
                ConnectionSeat[id] = seat;
                if (AdminId == null) AdminId = id;
                return (true,seat);
            }
            return (false,-1);
        }

        public void RemoveSeat(int seat)
        {
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p!=null)
            {
                Players.Remove(p);
                ConnectionSeat.Remove(p.Id);
                if (AdminId == p.Id)
                {
                    AdminId = Players.OrderBy(x=>x.Seat).FirstOrDefault()?.Id;
                }
            }
            if (ActiveSeat==seat)
            {
                ActiveSeat=null;
                if (Phase=="PLAY") AdvanceActive();
            }
        }

        public bool TryGetSeat(string id, out int seat)
        {
            return ConnectionSeat.TryGetValue(id, out seat);
        }

        public void StartGame()
        {
            Dealer.Clear();
            foreach(var p in Players){ p.Hand.Clear(); p.Stood=false; p.Finished=false; p.Hand1=null; p.Hand2=null; p.Finished1=false; p.Finished2=false; p.Bet1=p.Bet; p.Bet2=0; }
            Finished=false; Phase="PLAY";
            Deck = BuildDeck();
            Shuffle(Deck);
            for(int r=0;r<2;r++)
            {
                foreach(var p in Players){ p.Hand.Add(Deal()); DeckCount = Deck.Count; }
                Dealer.Add(Deal()); DeckCount = Deck.Count;
            }
            foreach(var p in Players)
            {
                if (IsBlackjack(p.Hand)) { p.Stood=true; p.Finished=true; }
            }
            var next = Players
                .Where(x=> (x.Hand2!=null ? !(x.Finished1 && x.Finished2) : !x.Finished))
                .OrderBy(x=>x.Seat)
                .FirstOrDefault();
            if (next!=null) ActiveSeat = next.Seat; else { DealerPlay(); Finished=true; Phase="SETTLEMENT"; Settle(); }
        }

        public void Hit(int seat)
        {
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished) return;
            p.Hand.Add(Deal()); DeckCount = Deck.Count;
            var sc = Score(p.Hand);
            if (sc>21){
                if (p.Hand2!=null && !p.Finished2 && p.Hand==p.Hand1){ p.Finished1=true; p.Hand=p.Hand2; p.Stood=false; p.Finished=false; return; }
                if (p.Hand2!=null && !p.Finished1 && p.Hand==p.Hand2){ p.Finished2=true; p.Stood=true; p.Finished=true; AdvanceActive(); return; }
                p.Finished=true; p.Stood=true; AdvanceActive();
            }
        }

        public void Stand(int seat)
        {
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished) return;
            if (p.Hand2!=null && p.Hand==p.Hand1 && !p.Finished2){ p.Finished1=true; p.Hand=p.Hand2; p.Stood=false; p.Finished=false; return; }
            if (p.Hand2!=null && p.Hand==p.Hand2){ p.Finished2=true; }
            p.Stood=true; p.Finished=true; AdvanceActive();
        }

        private void AdvanceActive()
        {
            var next = Players
                .Where(x=> x.Hand2!=null ? !(x.Finished1 && x.Finished2) : !x.Finished)
                .OrderBy(x=>x.Seat)
                .FirstOrDefault();
            if (next!=null){ ActiveSeat=next.Seat; return; }
            DealerPlay(); Finished=true; Phase="SETTLEMENT"; Settle();
        }

        private void DealerPlay()
        {
            while(Score(Dealer) < 17){ Dealer.Add(Deal()); DeckCount = Deck.Count; }
        }

        private List<Card> BuildDeck()
        {
            var d = new List<Card>();
            for(int k=0;k<DecksInShoe;k++)
            {
                foreach(var s in Suits)
                    foreach(var r in Ranks)
                        d.Add(new Card{ r=r, s=s });
            }
            return d;
        }

        private void Shuffle(List<Card> a)
        {
            for(int i=a.Count-1;i>0;i--){ int j=System.Security.Cryptography.RandomNumberGenerator.GetInt32(i+1); var t=a[i]; a[i]=a[j]; a[j]=t; }
        }

        private Card Deal(){ var c = Deck[^1]; Deck.RemoveAt(Deck.Count-1); return c; }

        private int ValueOf(Card c)
        {
            if (c.r=="A") return 11;
            if (c.r=="K"||c.r=="Q"||c.r=="J") return 10;
            return int.Parse(c.r);
        }

        private int Score(List<Card> hand)
        {
            int total=0; int aces=0;
            foreach(var c in hand){ total+=ValueOf(c); if(c.r=="A") aces++; }
            while(total>21 && aces>0){ total-=10; aces--; }
            return total;
        }

        private bool IsBlackjack(List<Card> hand)
        {
            return hand.Count==2 && Score(hand)==21;
        }

        public object ToDto()
        {
            return new {
                hostId = HostId,
                adminId = AdminId,
                dealer = Dealer,
                players = Players.Select(p=> new {
                    id=p.Id,
                    name=p.Name,
                    seat=p.Seat,
                    hand=p.Hand,
                    hand1=p.Hand1,
                    hand2=p.Hand2,
                    bet=p.Bet,
                    bet1=p.Bet1,
                    bet2=p.Bet2,
                    stood=p.Stood,
                    finished=p.Finished,
                    finished1=p.Finished1,
                    finished2=p.Finished2,
                    activeHand = (p.Hand2!=null && p.Hand==p.Hand2) ? 2 : 1,
                    money=p.Money,
                    score=Score(p.Hand),
                    score1=p.Hand1!=null ? Score(p.Hand1) : (int?)null,
                    score2=p.Hand2!=null ? Score(p.Hand2) : (int?)null
                }),
                activeSeat = ActiveSeat,
                finished = Finished,
                phase = Phase,
                deck = DeckCount
            };
        }

        public void NewBets()
        {
            Dealer.Clear();
            foreach(var p in Players){ p.Hand.Clear(); p.Stood=false; p.Finished=false; p.Bet=0; }
            Finished=false; ActiveSeat=null; Phase="BETTING"; Deck = BuildDeck(); Shuffle(Deck); DeckCount=Deck.Count;
        }

        public void PlaceBet(int seat, int amount)
        {
            if (Phase != "BETTING") return;
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null) return;
            if (amount <= 0) return;
            var capLeft = Math.Max(0, 2000 - p.Bet);
            var canSpend = Math.Max(0, p.Money);
            var delta = Math.Min(amount, Math.Min(capLeft, canSpend));
            if (delta<=0) return;
            p.Bet += delta;
            p.Money -= delta;
        }

        public void DoubleDown(int seat)
        {
            if (Phase != "PLAY") return;
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished) return;
            if (ActiveSeat != seat) return;
            if (p.Hand.Count != 2) return;
            var currentBet = (p.Hand2!=null && p.Hand==p.Hand2) ? p.Bet2 : (p.Hand1!=null ? p.Bet1 : p.Bet);
            if (currentBet <= 0) return;
            var extraCap = Math.Max(0, 2000 - (p.Bet1+p.Bet2));
            var extra = Math.Min(currentBet, Math.Min(extraCap, p.Money));
            if (extra<=0) return;
            if (p.Hand2!=null && p.Hand==p.Hand2){ p.Bet2 += extra; } else { if(p.Hand1!=null) p.Bet1 += extra; else p.Bet += extra; }
            p.Money -= extra;
            p.Hand.Add(Deal()); DeckCount = Deck.Count;
            if (p.Hand2!=null)
            {
                if (p.Hand==p.Hand1)
                {
                    p.Finished1 = true;
                    p.Hand = p.Hand2;
                    p.Stood = false;
                    p.Finished = false;
                }
                else
                {
                    p.Finished2 = true;
                    p.Stood = true;
                    p.Finished = true;
                    AdvanceActive();
                }
            }
            else
            {
                p.Stood = true; p.Finished = true; AdvanceActive();
            }
            p.Bet = p.Bet1 + p.Bet2;
        }

        public void Split(int seat)
        {
            if (Phase != "PLAY") return;
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished) return;
            if (ActiveSeat != seat) return;
            if (p.Hand.Count != 2) return;
            bool canPair = p.Hand[0].r==p.Hand[1].r || (ValueOf(p.Hand[0])==10 && ValueOf(p.Hand[1])==10);
            if (!canPair) return;
            var baseBet = (p.Bet1>0)?p.Bet1:p.Bet;
            var extraCap = Math.Max(0, 2000 - (p.Bet1+p.Bet2));
            var extra = Math.Min(baseBet, Math.Min(extraCap, p.Money));
            if (extra<=0) return;
            p.Money -= extra;
            p.Bet2 += extra;
            p.Hand1 = new List<Card>{ p.Hand[0] };
            p.Hand2 = new List<Card>{ p.Hand[1] };
            p.Hand = p.Hand1;
            p.Hand1.Add(Deal()); DeckCount = Deck.Count;
            p.Hand2.Add(Deal()); DeckCount = Deck.Count;
            p.Finished=false; p.Stood=false; p.Finished1=false; p.Finished2=false;
            p.Bet = p.Bet1 + p.Bet2;
        }

        private void Settle()
        {
            var dealerSc = Score(Dealer);
            var dealerBJ = IsBlackjack(Dealer);
            foreach(var p in Players)
            {
                int payout = 0;
                int payout2 = 0;
                Func<List<Card>,int,int> pay = (hand,bet)=>{
                    if (bet<=0) return 0;
                    var sc = Score(hand);
                    var playerBJ = IsBlackjack(hand);
                    if (sc>21) return 0;
                    if (dealerSc>21) return bet*2;
                    if (playerBJ && !dealerBJ) return (int)Math.Floor(bet*2.5);
                    if (dealerBJ && !playerBJ) return 0;
                    if (sc>dealerSc) return bet*2;
                    if (sc<dealerSc) return 0;
                    return bet; // push
                };
                if (p.Hand1!=null) payout = pay(p.Hand1, p.Bet1);
                else payout = pay(p.Hand, p.Bet1>0? p.Bet1 : p.Bet);
                if (p.Hand2!=null) payout2 = pay(p.Hand2, p.Bet2);
                var newMoney = p.Money + payout + payout2;
                if (newMoney < 0) newMoney = 0;
                if (newMoney > 1_000_000) newMoney = 1_000_000;
                p.Money = newMoney;
                p.Bet = 0; p.Bet1=0; p.Bet2=0;
                p.Hand1=null; p.Hand2=null; p.Finished1=false; p.Finished2=false;
            }
        }
    }
}
