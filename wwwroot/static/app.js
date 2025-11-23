const MAX_SEATS=5
let hub
let state={phase:'BETTING',deck:0,dealer:[],players:[],activeSeat:null,finished:false,hostId:null,adminId:null}
let prevState=null
const ui={stood:false}

function isAdmin(){
  const myId = hub && hub.connectionId
  return !!myId && (state.adminId===myId || state.hostId===myId)
}

function score(hand){
  let total=0, aces=0
  for(const c of hand){
    total+= (c.r==='A'?11: (c.r==='K'||c.r==='Q'||c.r==='J'?10: parseInt(c.r)))
    if(c.r==='A') aces++
  }
  while(total>21 && aces>0){ total-=10; aces-- }
  return total
}

function suitClass(s){ return (s==='♥'||s==='♦')?'red':'black' }

function cardEl(card, faceUp=true, idx=0, animate=false){
  const el=document.createElement('div');el.className=faceUp?'card front':'card back'
  if(animate){ el.classList.add('deal'); el.style.animationDelay=`${idx*0.15}s` }
  if(faceUp){
    const cls=suitClass(card.s)
    const tl=document.createElement('div');tl.className=`pip tl ${cls}`;tl.textContent=`${card.r}${card.s}`
    const br=document.createElement('div');br.className=`pip br ${cls}`;br.textContent=`${card.r}${card.s}`
    const rank=document.createElement('div');rank.className=`rank ${cls}`;rank.textContent=card.r
    el.appendChild(tl);el.appendChild(br);el.appendChild(rank)
  }
  return el
}

function spreadArc(start,end,n){ const step=(end-start)/(n-1); return Array.from({length:n},(_,i)=>start+i*step) }

function render(){
  const connected = hub && hub.state===signalR.HubConnectionState.Connected
  document.getElementById('phase').textContent= connected ? (state.phase||'BETTING') : 'OFFLINE'
  document.getElementById('deck').textContent= connected ? String(state.deck||0) : '-'
  if(state.phase==='SETTLEMENT'){ ui.stood=false }

  const seatsEl=document.getElementById('seats');seatsEl.innerHTML=''
  const dealerEl=document.getElementById('dealer');dealerEl.innerHTML=''

  const table=document.querySelector('.table-area')
  const rect=table.getBoundingClientRect()
  const cx=rect.width/2, cy=rect.height/2
  const rx=cx-160, ry=cy-110

  const dealerCards=document.createElement('div');dealerCards.className='cards'
  const dealerScore=document.createElement('div');dealerScore.className='meta'
  const dealerFaceDown=(state.phase==='PLAY' && !state.finished)
  const prevDealerLen = prevState ? (prevState.dealer ? prevState.dealer.length : 0) : 0
  state.dealer.forEach((c,idx)=> {
    const animate = !prevState || idx>=prevDealerLen
    const faceUp = !(dealerFaceDown && idx===1)
    dealerCards.appendChild(cardEl(c, faceUp, idx, animate))
  })
  dealerScore.textContent=`Krupier • ${state.finished? score(state.dealer): (dealerFaceDown? '?': score(state.dealer))}`
  dealerEl.appendChild(dealerCards)
  dealerEl.appendChild(dealerScore)

  const present = state.players.slice().sort((a,b)=>a.seat-b.seat)
  const n = present.length
  const isMobile = window.innerWidth<=640
  const seatWidth = isMobile ? 120 : 160
  const gap = isMobile ? 16 : 24
  const ovalRect=document.querySelector('.oval').getBoundingClientRect()
  const tableRect=rect
  const ovalLeft = ovalRect.left - tableRect.left
  const ovalWidth = ovalRect.width
  const totalWidth = n>0 ? (n*seatWidth + (n-1)*gap) : 0
  const startX = ovalLeft + (ovalWidth - totalWidth)/2
  const seatHConst = 120
  const ovalBottomY = ovalRect.bottom - tableRect.top
  const targetTop = Math.max(0, ovalBottomY - seatHConst/2 - 10)

  for(let idx=0; idx<n; idx++){
    const p=present[idx]
    const left = startX + idx*(seatWidth+gap)
    const seat=document.createElement('div');seat.className='seat'
    seat.style.left=`${left}px`
    seat.style.top = `${targetTop}px`
    const name=document.createElement('div');name.className='name'
    const nm=document.createElement('span');nm.textContent=(p.name||'').slice(0,10)
    name.appendChild(nm)
    const meta=document.createElement('div');meta.className='meta';meta.innerHTML=`<span>Miejsce ${p.seat+1}</span><span>Wartość: ${p.score!=null?p.score:score(p.hand)}</span>`
    const cards=document.createElement('div');cards.className='cards'
    const prevP = prevState ? prevState.players.find(pp=>pp.seat===p.seat) : null
    const prevLen = prevP ? (prevP.hand ? prevP.hand.length : 0) : 0
    p.hand.forEach((c,i)=> {
      const animate = !prevState || i>=prevLen
      cards.appendChild(cardEl(c,true,i,animate))
    })
    const chips=document.createElement('div');chips.className='chips-row'
    addChips(chips, p.bet||0)
    name.appendChild(chips)
    seat.appendChild(name)
    seat.appendChild(meta)
    seat.appendChild(cards)
    seatsEl.appendChild(seat)
  }

  setControls()
  // balans w HUD
  const balEl=document.getElementById('balance')
  const myId=(hub && hub.connectionId) || state.hostId || state.adminId
  const me=state.players.find(p=>p.id===myId)
  if(balEl){ balEl.textContent = me? String(me.money) : '-' }
  renderBurst()
  renderResult()
}

function renderBurst(){
  const banner=document.getElementById('burst')
  const meId=hub && hub.connectionId
  const me=state.players.find(p=>p.id===meId)
  const bust=me && me.finished && (me.score!=null?me.score:score(me.hand))>21
  banner.style.display=bust?'block':'none'
}

function renderResult(){
  const modal=document.getElementById('result')
  const title=document.getElementById('result-title')
  const sub=document.getElementById('result-sub')
  const meId=hub && hub.connectionId
  const me=state.players.find(p=>p.id===meId)
  if(state.phase!=='SETTLEMENT' || !me){ modal.classList.add('hidden'); return }
  const dealerSc=score(state.dealer)
  const mySc=me.score!=null?me.score:score(me.hand)
  const prevMe = prevState ? prevState.players.find(p=>p.id===meId) : null
  const betBefore = prevMe ? (prevMe.bet||0) : 0
  const moneyBefore = prevMe ? (prevMe.money||0) : me.money
  const delta = me.money - moneyBefore
  const pct = betBefore>0 ? Math.round((delta/betBefore)*100) : 0
  let outcome, detail
  if(mySc>21){ outcome='Przegrana'; detail=`Burst (${mySc})` }
  else if(dealerSc>21){ outcome='Wygrana'; detail=`Krupier spalił (${dealerSc})` }
  else if(mySc>dealerSc){ outcome='Wygrana'; detail=`${mySc} > ${dealerSc}` }
  else if(mySc<dealerSc){ outcome='Przegrana'; detail=`${mySc} < ${dealerSc}` }
  else { outcome='Remis'; detail=`${mySc} = ${dealerSc}` }
  title.textContent=`${outcome} • ${delta>0?'+':''}${delta}`
  const stakeText = betBefore ? `Stawka: ${betBefore}` : ''
  const pctText = betBefore ? `(${pct>0?'+':''}${pct}%)` : ''
  const balText = `Balans: ${me.money}`
  sub.textContent=`${detail} • ${stakeText} ${pctText} • ${balText}`
  modal.classList.remove('hidden')
}

function placeHand(hand, angleDeg, cx, cy, rx, ry, deckX, deckY, canvas){
  const rad=angleDeg*Math.PI/180
  const baseX=cx+rx*Math.cos(rad)
  const baseY=cy+ry*Math.sin(rad)
  for(let i=0;i<hand.length;i++){
    const c=hand[i]
    const el=cardEl(c)
    const x=baseX-34 + i*22
    const y=baseY-46 - Math.abs(Math.sin(rad))*6
    el.style.left=`${x}px`
    el.style.top=`${y}px`
    const dx=deckX-x, dy=deckY-y
    el.style.setProperty('--dx', `${dx}px`)
    el.style.setProperty('--dy', `${dy}px`)
    el.style.animation='deal .45s ease-out both'
    el.style.animationDelay=`${i*0.12}s`
    canvas.appendChild(el)
  }
}


function setControls(){
  const meId = hub.connection && hub.connection.connectionId
  if(!meId && hub && hub.connectionId){
  }
  const myId = meId || (hub && hub.connectionId)
  const me=state.players.find(p=>p.id===myId)
  if(state.phase!=='PLAY'){ ui.stood=false }
  if(state.phase==='PLAY' && me && me.hand && me.hand.length===2 && (me.score!=null?me.score:score(me.hand))===21){ ui.stood=true }
  const canPlay=!!me && !ui.stood && state.activeSeat===me.seat && !state.finished && !me.finished && state.phase==='PLAY'
  document.querySelectorAll('.play').forEach(b=>{b.style.display=state.phase==='PLAY'?'inline-block':'none';b.disabled=!canPlay})

  const canStart=isAdmin() && state.phase==='BETTING' && state.players.some(p=>(p.bet||0)>0)
  const startBtn=document.getElementById('start')
  startBtn.style.display= (isAdmin() && state.phase==='BETTING') ? 'inline-block' : 'none'
  startBtn.disabled=!canStart

  const betsBtn=document.getElementById('bets')
  betsBtn.style.display= isAdmin() ? 'inline-block' : 'none'
  betsBtn.disabled=!isAdmin() || ui.stood
  if(ui.stood){ betsBtn.textContent='Czekaj...' } else { betsBtn.textContent='Nowe zakłady' }

  const resetBtn=document.getElementById('reset')
  resetBtn.style.display= isAdmin() ? 'inline-block' : 'none'
  resetBtn.disabled = ui.stood || state.phase==='PLAY'
  resetBtn.textContent = resetBtn.disabled? 'Czekaj...' : 'Reset strony'

  document.querySelector('.chips-bar').classList.toggle('hidden', state.phase!=='BETTING')
  const chipBtns=[...document.querySelectorAll('.chip')]
  chipBtns.forEach(b=>{
    const amt=Number(b.getAttribute('data-amt'))||0
    const overCap = me ? (me.bet||0)+amt > 2000 : true
    b.disabled = state.phase!=='BETTING' || overCap
  })

  const joinBox=document.querySelector('.hud .join')
  joinBox.style.display = me ? 'none' : 'flex'
}

document.querySelectorAll('.chip').forEach(btn=>{
  btn.addEventListener('click',()=>{const amt=Number(btn.getAttribute('data-amt'));hub.invoke('PlaceBet',amt)})
})

function clickFX(btn){ if(!btn) return; btn.classList.add('clicked'); setTimeout(()=>btn.classList.remove('clicked'),150) }
document.getElementById('start').addEventListener('click',(e)=>{clickFX(e.target); hub.invoke('StartGame')})
document.getElementById('bets').addEventListener('click',(e)=>{clickFX(e.target); hub.invoke('NewBets'); ui.stood=false })
document.getElementById('hit').addEventListener('click',(e)=>{clickFX(e.target); document.querySelectorAll('.play').forEach(b=>b.disabled=true); hub.invoke('Hit')})
document.getElementById('stand').addEventListener('click',(e)=>{clickFX(e.target); ui.stood=true; document.querySelectorAll('.play').forEach(b=>{b.disabled=true; b.style.display='none'}); const reset=document.getElementById('reset'); if(reset){ reset.disabled=true; reset.textContent='Czekaj...' } hub.invoke('Stand'); setControls()})
document.getElementById('double').addEventListener('click',(e)=>{clickFX(e.target); document.querySelectorAll('.play').forEach(b=>b.disabled=true); hub.invoke('DoubleDown')})

async function start(){
  hub=new signalR.HubConnectionBuilder().withUrl(window.location.origin+'/gamehub').withAutomaticReconnect().build()
  hub.on('State',s=>{prevState=state;state=s;render()})
  hub.on('Welcome',()=>{ /* noop, but confirms connection */ })
  hub.on('JoinFailed',msg=>{ try{ alert(msg) }catch(e){} })
  hub.onreconnecting(()=>{
    const phaseEl=document.getElementById('phase'); if(phaseEl) phaseEl.textContent='Łączenie...'
    const deckEl=document.getElementById('deck'); if(deckEl) deckEl.textContent='-'
    const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=true
  })
  hub.onreconnected(()=>{
    const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=false
  })
  hub.onclose(()=>{
    const phaseEl=document.getElementById('phase'); if(phaseEl) phaseEl.textContent='OFFLINE'
    const deckEl=document.getElementById('deck'); if(deckEl) deckEl.textContent='-'
    const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=true
  })
  try{
    await hub.start()
    const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=false
  }catch(e){
    const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=true
  }
}

start()

const joinBtn=document.getElementById('join'); if(joinBtn) joinBtn.disabled=true
document.getElementById('join').addEventListener('click',async()=>{
  const name=document.getElementById('name').value.trim()||'Gracz'
  const connected = hub && hub.state===signalR.HubConnectionState.Connected
  if(connected){ await hub.invoke('Join', name) }
  else {
    try{ await hub.start(); await hub.invoke('Join', name) }catch(e){ /* ignore */ }
  }
})

document.getElementById('result-close').addEventListener('click',()=>{
  document.getElementById('result').classList.add('hidden')
})

document.getElementById('reset').addEventListener('click',()=>{ window.location.reload() })
function addChips(container, amount){
  const denoms=[500,100,50,25,10]
  let remaining=amount
  for(const d of denoms){
    while(remaining>=d){
      const chip=document.createElement('div')
      chip.className=`chip-token chip-t-${d}`
      chip.textContent=String(d)
      chip.style.position='relative'
      chip.style.width='28px';chip.style.height='28px'
      chip.style.borderWidth='3px'
      container.appendChild(chip)
      remaining-=d
    }
  }
}
