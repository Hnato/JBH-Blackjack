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
            bool triggerDealer = false;
            lock (_lock)
            {
                State.StartGame();
                triggerDealer = (State.ActiveSeat == null && State.Phase == "PLAY");
            }
            await Clients.All.SendAsync("State", State.ToDto());
            if (triggerDealer) await RunDealerSequence();
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
            bool triggerDealer = false;
            lock (_lock)
            {
                if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
                State.Hit(seat);
                triggerDealer = (State.ActiveSeat == null && State.Phase == "PLAY");
            }
            await Clients.All.SendAsync("State", State.ToDto());
            if (triggerDealer) await RunDealerSequence();
        }

        public async Task Stand()
        {
            bool triggerDealer = false;
            lock (_lock)
            {
                if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
                State.Stand(seat);
                triggerDealer = (State.ActiveSeat == null && State.Phase == "PLAY");
            }
            await Clients.All.SendAsync("State", State.ToDto());
            if (triggerDealer) await RunDealerSequence();
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
            bool triggerDealer = false;
            lock (_lock)
            {
                if (!State.TryGetSeat(Context.ConnectionId, out var seat)) return;
                State.DoubleDown(seat);
                triggerDealer = (State.ActiveSeat == null && State.Phase == "PLAY");
            }
            await Clients.All.SendAsync("State", State.ToDto());
            if (triggerDealer) await RunDealerSequence();
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

        private async Task RunDealerSequence()
        {
            lock (_lock)
            {
                if (State.Phase != "PLAY") return;
                if (State.DealerActing) return;
                State.DealerActing = true;
            }

            try
            {
                while (true)
                {
                    await Task.Delay(1000);
                    bool stay = false;
                    lock (_lock)
                    {
                        if (State.Phase != "PLAY") return;
                        if (State.Score(State.Dealer) >= 17)
                        {
                            stay = true;
                        }
                        else
                        {
                            State.Dealer.Add(State.Deal());
                        }
                    }
                    await Clients.All.SendAsync("State", State.ToDto());
                    if (stay) break;
                }

                lock (_lock)
                {
                    State.Finished = true;
                    State.Phase = "SETTLEMENT";
                    try
                    {
                        State.Settle();
                    }
                    catch (Exception ex)
                    {
                        foreach(var p in State.Players)
                        {
                            if (string.IsNullOrEmpty(p.WinReason)) p.WinReason = "Błąd rozliczenia";
                        }
                    }
                }
                await Clients.All.SendAsync("State", State.ToDto());
            }
            finally
            {
                lock (_lock)
                {
                    State.DealerActing = false;
                }
            }
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
        public int LastWin { get; set; }
        public int LastBet { get; set; }
        public string WinReason { get; set; } = "";
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
        public bool DealerActing { get; set; }
        public string Phase { get; set; } = "BETTING";
        
        public GameState()
        {
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
            foreach(var p in Players){ p.Hand.Clear(); p.Stood=false; p.Finished=false; p.Hand1=null; p.Hand2=null; p.Finished1=false; p.Finished2=false; p.Bet1=p.Bet; p.Bet2=0; p.LastWin=0; p.LastBet=0; p.WinReason=""; }
            Finished=false; Phase="PLAY"; DealerActing=false;
            for(int r=0;r<2;r++)
            {
                foreach(var p in Players){ 
                    if(p.Bet > 0) p.Hand.Add(Deal()); 
                    else p.Finished=true;
                }
                Dealer.Add(Deal());
            }
            foreach(var p in Players)
            {
                if (p.Hand.Count > 0 && IsBlackjack(p.Hand)) { p.Stood=true; p.Finished=true; }
            }
            var next = Players
                .Where(x=> (x.Hand.Count > 0) && (x.Hand2!=null ? !(x.Finished1 && x.Finished2) : !x.Finished))
                .OrderBy(x=>x.Seat)
                .FirstOrDefault();
            if (next!=null) ActiveSeat = next.Seat; else ActiveSeat = null; 
        }

        public void Hit(int seat)
        {
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished || p.Hand.Count == 0) return;
            p.Hand.Add(Deal());
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
            ActiveSeat = null;
        }

        public Card Deal()
        {
            var r = Ranks[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Ranks.Length)];
            var s = Suits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Suits.Length)];
            return new Card { r = r, s = s };
        }

        private int ValueOf(Card c)
        {
            if (c.r=="A") return 11;
            if (c.r=="K"||c.r=="Q"||c.r=="J") return 10;
            return int.Parse(c.r);
        }

        public int Score(List<Card> hand)
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
                    lastWin=p.LastWin,
                    lastBet=p.LastBet,
                    winReason=p.WinReason,
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
                dealerScore = Score(Dealer),
                deck = 9999
            };
        }

        public void NewBets()
        {
            Dealer.Clear();
            foreach(var p in Players){ p.Hand.Clear(); p.Stood=false; p.Finished=false; p.Bet=0; p.LastWin=0; p.LastBet=0; p.WinReason=""; }
            Finished=false; ActiveSeat=null; Phase="BETTING";
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
            if (p.Hand2!=null && p.Hand==p.Hand2){ p.Bet2 += extra; } else { if(p.Hand1!=null) p.Bet1 += extra; else { p.Bet += extra; p.Bet1 += extra; } }
            p.Money -= extra;
            p.Hand.Add(Deal());
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
            p.Hand1.Add(Deal());
            p.Hand2.Add(Deal());
            p.Finished=false; p.Stood=false; p.Finished1=false; p.Finished2=false;
            p.Bet = p.Bet1 + p.Bet2;
        }
        public void Settle()
        {
            var dealerSc = Score(Dealer);
            var dealerBJ = IsBlackjack(Dealer);

            foreach(var p in Players)
            {
                try
                {
                    int totalPayout = 0;
                    string r1 = "";
                    string r2 = "";

                    // Local helper to calculate outcome for one hand
                    (int payout, string reason) ResolveHand(List<Card> hand, int bet)
                    {
                        if (bet <= 0 || hand == null || hand.Count == 0) return (0, "");

                        var sc = Score(hand);
                        var isBJ = IsBlackjack(hand);

                        // 1. Player Busts always lose
                        if (sc > 21) return (0, $"Spaliłeś ({sc})");

                        // 2. Player Blackjack
                        if (isBJ)
                        {
                            // If Dealer also has BJ -> Push
                            if (dealerBJ) return (bet, "Remis (Blackjack)");
                            // Else Player wins 3:2
                            return ((int)Math.Floor(bet * 2.5), "Blackjack");
                        }

                        // 3. Dealer Busts (Player did not bust) -> Player Wins
                        if (dealerSc > 21) return (bet * 2, $"Krupier spalił ({dealerSc}) [B:{bet} W:{bet*2}]");

                        // 4. Dealer has Blackjack (Player does not) -> Player Loses
                        if (dealerBJ) return (0, "Blackjack Krupiera");

                        // 5. Compare Scores
                        if (sc > dealerSc) return (bet * 2, $"Wygrana ({sc} > {dealerSc}) [B:{bet} W:{bet*2}]");
                        if (sc < dealerSc) return (0, $"Przegrana ({sc} < {dealerSc})");
                        
                        // 6. Tie
                        return (bet, $"Remis ({sc} = {dealerSc}) [B:{bet} W:{bet}]");
                    }

                    // Process Hand 1 (Split) OR Main Hand
                    if (p.Hand1 != null)
                    {
                        var res = ResolveHand(p.Hand1, p.Bet1);
                        totalPayout += res.payout;
                        r1 = res.reason;
                    }
                    else
                    {
                        var res = ResolveHand(p.Hand, p.Bet);
                        totalPayout += res.payout;
                        r1 = res.reason;
                    }

                    // Process Hand 2 (Split)
                    if (p.Hand2 != null)
                    {
                        var res = ResolveHand(p.Hand2, p.Bet2);
                        totalPayout += res.payout;
                        r2 = res.reason;
                    }

                    // Update Player Money & Stats
                    long newMoney = (long)p.Money + totalPayout;
                    if (newMoney > 1_000_000) newMoney = 1_000_000;
                    p.Money = (int)newMoney;

                    p.LastWin = totalPayout;
                    p.LastBet = (p.Hand1 != null || p.Hand2 != null) ? (p.Bet1 + p.Bet2) : p.Bet;

                    if (p.Hand2 != null) p.WinReason = $"{r1} | {r2}";
                    else p.WinReason = r1;

                    if (string.IsNullOrEmpty(p.WinReason)) p.WinReason = "Rozliczono";
                }
                catch (Exception ex)
                {
                    p.WinReason = $"Błąd: {ex.Message}";
                    p.LastWin = 0;
                }
                finally
                {
                    // Reset round state for player
                    p.Bet = 0; p.Bet1 = 0; p.Bet2 = 0;
                    p.Hand1 = null; p.Hand2 = null;
                    p.Finished1 = false; p.Finished2 = false;
                }
            }
        }
    }
}
