using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

public class GameHub : Hub
{
    private static readonly object _lock = new object();
    private static readonly string[] Suits = new[] { "♠", "♥", "♦", "♣" };
    private static readonly string[] Ranks = new[] { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    private static GameState State = new GameState();
    private readonly IHubContext<GameHub> _hubContext;

    public GameHub(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

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
        string? errorMsg;
        lock (_lock)
        {
            (ok, assignedSeat, errorMsg) = State.AddPlayer(Context.ConnectionId, name);
        }
        if (!ok)
        {
            await Clients.Caller.SendAsync("JoinFailed", errorMsg);
            return;
        }
        
        // If I am the admin (just assigned), send the token
        string? tokenToSend = null;
        lock (_lock)
        {
            if (State.AdminId == Context.ConnectionId && State.AdminToken != null)
            {
                tokenToSend = State.AdminToken;
            }
        }
        if (tokenToSend != null)
        {
            await Clients.Caller.SendAsync("PromoteToAdmin", tokenToSend);
        }

        await Clients.All.SendAsync("State", State.ToDto());
    }

    public async Task ClaimAdmin(string token)
    {
        bool success = false;
        lock (_lock)
        {
            if (State.AdminToken == token)
            {
                State.AdminId = Context.ConnectionId;
                success = true;
            }
        }
        if (success)
        {
            await Clients.Caller.SendAsync("PromoteToAdmin", token); // Refresh token just in case
            await Clients.All.SendAsync("State", State.ToDto());
        }
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
            if (triggerDealer) _ = RunDealerSequence();
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

        public async Task ResetGame() {}


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
            if (triggerDealer) _ = RunDealerSequence();
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
            if (triggerDealer) _ = RunDealerSequence();
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
            if (triggerDealer) _ = RunDealerSequence();
        }

        public async Task<object> Split(int seat)
        {
            bool success = false;
            string message = "";
            bool triggerDealer = false;

            lock (_lock)
            {
                if (!State.TryGetSeat(Context.ConnectionId, out var actualSeat)) 
                    return new { success=false, message="Błąd: Brak gracza" };

                if (actualSeat != seat) 
                    return new { success=false, message="Błąd: Nieprawidłowe miejsce" };

                (success, message) = State.Split(seat);
                triggerDealer = (State.ActiveSeat == null && State.Phase == "PLAY");
            }

            if (success)
            {
                await Clients.All.SendAsync("State", State.ToDto());
            }

            if (triggerDealer) _ = RunDealerSequence();

            return new { success, message };
        }

        private async Task RunDealerSequence()
        {
            bool skipLoop = false;
            lock (_lock)
            {
                if (State.Phase != "PLAY") return;
                if (State.DealerActing) return;
                State.DealerActing = true;
                
                var anyLive = State.Players.Any(p => {
                    if (p.Hand1 != null || p.Hand2 != null) {
                        return (p.Hand1 != null && State.Score(p.Hand1) <= 21) || 
                               (p.Hand2 != null && State.Score(p.Hand2) <= 21);
                    }
                    return State.Score(p.Hand) <= 21;
                });
                
                if (!anyLive) {
                    skipLoop = true;
                    State.GameLog.Add("Wszyscy spalili - koniec rundy.");
                } else {
                    State.GameLog.Add("--- Tura Krupiera ---");
                }
            }
            
            if (!skipLoop) await Task.Delay(50);
            await _hubContext.Clients.All.SendAsync("State", State.ToDto());

            try
            {
                if (!skipLoop)
                {
                    while (true)
                    {
                        await Task.Delay(1000); 
                        bool shouldStay = false;
                        lock (_lock)
                        {
                            if (State.Phase != "PLAY") return;
                            var score = State.Score(State.Dealer);
                            if (score >= 17) {
                                shouldStay = true;
                                State.GameLog.Add($"Krupier pasuje: {score}");
                            } else {
                                var card = State.Deal();
                                State.Dealer.Add(card);
                                var newScore = State.Score(State.Dealer);
                                State.GameLog.Add($"Krupier dobiera: {card.r}{card.s} (Razem: {newScore})");
                            }
                        }
                        await _hubContext.Clients.All.SendAsync("State", State.ToDto());
                        if (shouldStay) break;
                    }
                }

                lock (_lock)
                {
                    State.Finished = true;
                    State.Phase = "SETTLEMENT";
                    State.GameLog.Add("--- Rozliczanie ---");
                    try { State.Settle(); }
                    catch (Exception ex) {
                        State.GameLog.Add($"BŁĄD: {ex.Message}");
                    }
                    State.GameLog.Add("Nowa runda za 10 sekund...");
            }
            await _hubContext.Clients.All.SendAsync("State", State.ToDto());

            await Task.Delay(10000);

            bool restarted = false;
                lock (_lock)
                {
                    if (State.Phase == "SETTLEMENT")
                    {
                        State.NewBets();
                        restarted = true;
                    }
                }
                if (restarted)
                {
                    await _hubContext.Clients.All.SendAsync("State", State.ToDto());
                }
            }
            finally
            {
                lock (_lock) { State.DealerActing = false; }
            }
        }

    private bool IsHost(HubCallerContext ctx)
    {
        var http = ctx.GetHttpContext();
        var ip = http?.Connection.RemoteIpAddress;
        if (ip != null && (System.Net.IPAddress.IsLoopback(ip))) return true;
        if (State.AdminId != null) return State.AdminId == ctx.ConnectionId;
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
        public List<string> GameLog { get; set; } = new List<string>();
        public string? AdminToken { get; set; }
        
        public GameState()
        {
        }

        public (bool,int,string?) AddPlayer(string id, string name)
        {
            if (ConnectionSeat.ContainsKey(id))
            {
                return (false, -1, "Już jesteś w grze");
            }
            var nm = (name??"").Trim(); if (nm.Length>10) nm = nm.Substring(0,10);
            
            if (Players.Any(p => p.Name.Equals(nm, StringComparison.InvariantCultureIgnoreCase)))
            {
                return (false, -1, "Nick jest zajęty");
            }

            for(int seat=0; seat<5; seat++)
            {
                if (Players.Any(p=>p.Seat==seat)) continue;
                var p = new Player{ Id=id, Name=nm, Seat=seat, Money=1000 };
                Players.Add(p);
                ConnectionSeat[id] = seat;
                
                // Only assign Admin if no token exists (fresh game)
                if (AdminId == null && AdminToken == null) 
                {
                    AdminId = id;
                    AdminToken = Guid.NewGuid().ToString();
                }
                return (true,seat,null);
            }
            return (false,-1, "Brak wolnych miejsc");
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
                    AdminId = null;
                }
            }
            if (ActiveSeat==seat)
            {
                ActiveSeat=null;
                if (Phase=="PLAY") AdvanceActive();
            }

            if (Players.Count == 0)
            {
                Dealer.Clear();
                Phase = "BETTING";
                Finished = false;
                DealerActing = false;
                GameLog.Clear();
                ActiveSeat = null;
                AdminId = null;
                AdminToken = null;
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
            if (p==null || p.Finished) return;
            
            // Safety check
            if (p.Hand == null) return;

            p.Hand.Add(Deal());
            var sc = Score(p.Hand);
            
            if (sc > 21)
            {
                // Handle Bust
                if (p.Hand2 != null) // Split Mode
                {
                    if (p.Hand == p.Hand1)
                    {
                        // Hand 1 busted -> Move to Hand 2
                        GameLog.Add($"{p.Name} (Ręka 1): Fura ({sc})");
                        p.Finished1 = true;
                        if (p.Finished2)
                        {
                            p.Finished = true;
                            p.Stood = true;
                            AdvanceActive();
                        }
                        else
                        {
                            p.Hand = p.Hand2;
                        }
                    }
                    else
                    {
                        // Hand 2 busted -> Finish
                        GameLog.Add($"{p.Name} (Ręka 2): Fura ({sc})");
                        p.Finished2 = true;
                        p.Finished = true;
                        p.Stood = true;
                        AdvanceActive();
                    }
                }
                else
                {
                    // Normal Mode
                    GameLog.Add($"{p.Name}: Fura ({sc})");
                    p.Finished = true;
                    p.Stood = true;
                    AdvanceActive();
                }
            }
        }

        public void Stand(int seat)
        {
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null || p.Finished) return;

            if (p.Hand2 != null) // Split Mode
            {
                if (p.Hand == p.Hand1)
                {
                    // Stand on Hand 1 -> Move to Hand 2
                    var sc = Score(p.Hand1);
                    GameLog.Add($"{p.Name} (Ręka 1): Pas ({sc})");
                    p.Finished1 = true;
                    if (p.Finished2)
                    {
                        p.Finished = true;
                        p.Stood = true;
                        AdvanceActive();
                    }
                    else
                    {
                        p.Hand = p.Hand2;
                    }
                }
                else
                {
                    // Stand on Hand 2 -> Finish
                    var sc = Score(p.Hand2);
                    GameLog.Add($"{p.Name} (Ręka 2): Pas ({sc})");
                    p.Finished2 = true;
                    p.Finished = true;
                    p.Stood = true;
                    AdvanceActive();
                }
            }
            else
            {
                // Normal Mode
                var sc = Score(p.Hand);
                GameLog.Add($"{p.Name}: Pas ({sc})");
                p.Finished = true;
                p.Stood = true;
                AdvanceActive();
            }
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
                dealerActing = DealerActing,
                phase = Phase,
                dealerScore = Score(Dealer),
                deck = 9999,
                gameLog = GameLog
            };
        }

        public void NewBets()
        {
            Dealer.Clear();
            GameLog.Clear();
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
        public (bool, string) Split(int seat)
        {
            if (Phase != "PLAY") return (false, "Błąd Split: zła faza");
            var p = Players.FirstOrDefault(x=>x.Seat==seat);
            if (p==null) return (false, "Błąd Split: gracz nieznany");
            if (p.Finished) return (false, "Błąd Split: tura zakończona");
            if (ActiveSeat != seat) return (false, "Błąd Split: nie twoja kolej");
            
            if (p.Hand == null || p.Hand.Count != 2) return (false, "Błąd Split: wymagane 2 karty");
            if (p.Hand1 != null || p.Hand2 != null) return (false, "Błąd Split: już rozdzielono");

            var c1 = p.Hand[0];
            var c2 = p.Hand[1];
            bool isPair = (c1.r == c2.r) || (ValueOf(c1) == 10 && ValueOf(c2) == 10);
            if (!isPair) return (false, "Błąd Split: karty nie są parą");
            
            var baseBet = (p.Bet1>0)?p.Bet1:p.Bet;
            var extraCap = Math.Max(0, 2000 - (p.Bet1+p.Bet2));
            var extra = Math.Min(baseBet, Math.Min(extraCap, p.Money));
            
            if (extra < baseBet) { 
                return (false, $"Błąd Split: brak środków ({p.Money} < {baseBet})"); 
            }
            if (extra<=0) return (false, "Błąd Split: nieprawidłowa stawka");
            
            GameLog.Add($"{p.Name} rozdziela karty!");
            
            p.Money -= extra;
            
            // Create separate hands
            p.Hand1 = new List<Card>{ c1 };
            p.Hand2 = new List<Card>{ c2 };
            
            // Deal second card to each
            p.Hand1.Add(Deal());
            p.Hand2.Add(Deal());
            
            // Set bets
            p.Bet1 = baseBet;
            p.Bet2 = extra;
            p.Bet = p.Bet1 + p.Bet2;
            
            // Reset state
            p.Finished=false; 
            p.Stood=false; 
            p.Finished1=false; 
            p.Finished2=false;
            
            // Set Active Hand to Hand1
            p.Hand = p.Hand1;

            // Check for instant Blackjack on split hands
            bool bj1 = IsBlackjack(p.Hand1);
            bool bj2 = IsBlackjack(p.Hand2);

            if (bj1)
            {
                p.Finished1 = true;
                GameLog.Add($"{p.Name} (Ręka 1): Blackjack!");
                p.Hand = p.Hand2;
            }
            if (bj2)
            {
                p.Finished2 = true;
                GameLog.Add($"{p.Name} (Ręka 2): Blackjack!");
            }

            if (p.Finished1 && p.Finished2)
            {
                p.Finished = true;
                p.Stood = true;
                AdvanceActive();
            }
            
            return (true, "OK");
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
                        if (dealerSc > 21) return (bet * 2, $"Krupier spalił ({dealerSc})");

                        // 4. Dealer has Blackjack (Player does not) -> Player Loses
                        if (dealerBJ) return (0, "Blackjack Krupiera");

                        // 5. Compare Scores
                        if (sc > dealerSc) return (bet * 2, $"Wygrana ({sc} > {dealerSc})");
                        if (sc < dealerSc) return (0, $"Przegrana ({sc} < {dealerSc})");
                        
                        // 6. Tie
                        return (bet, $"Remis ({sc} = {dealerSc})");
                    }

                    // Process Hand 1 (Split) OR Main Hand
                    if (p.Hand1 != null)
                    {
                        var res = ResolveHand(p.Hand1, p.Bet1);
                        totalPayout += res.payout;
                        r1 = res.reason;
                        GameLog.Add($"{p.Name} (Ręka 1): {res.reason} [Zakład:{p.Bet1} -> Wygrana:{res.payout}]");
                    }
                    else
                    {
                        var res = ResolveHand(p.Hand, p.Bet);
                        totalPayout += res.payout;
                        r1 = res.reason;
                        GameLog.Add($"{p.Name}: {res.reason} [Zakład:{p.Bet} -> Wygrana:{res.payout}]");
                    }

                    // Process Hand 2 (Split)
                    if (p.Hand2 != null)
                    {
                        var res = ResolveHand(p.Hand2, p.Bet2);
                        totalPayout += res.payout;
                        r2 = res.reason;
                        GameLog.Add($"{p.Name} (Ręka 2): {res.reason} [Zakład:{p.Bet2} -> Wygrana:{res.payout}]");
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
