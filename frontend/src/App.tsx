import { useEffect, useMemo, useState } from 'react'

type Match = {
  id: number; home: string; away: string; date: string; round: string; homeForm: string; awayForm: string;
  homeGoals: string; awayGoals: string; probabilities: { home: number; draw: number; away: number };
  bands: { home: string; away: string; total: string }; homeCrest?: string | null; awayCrest?: string | null;
  homeScore?: number | null; awayScore?: number | null
}
type RecentMatch = { date: string; opponent: string; goalsFor: number; goalsAgainst: number; atHome: boolean }
type MatchDetails = { match: Match; homeRecent: RecentMatch[]; awayRecent: RecentMatch[] }
type MatchState = { matches: Match[]; history: Match[]; loading: boolean; error: string | null; provider: string }
type Pick = { match: Match; label: string; probability: number }
type TicketProfile = 'medium' | 'risky'
type Ticket = { key: string; title: string; profile: TicketProfile; picks: Pick[] }

const Form = ({ value }: { value: string }) => <span className="form">{value.split(' ').map((x, i) => <i key={i} className={x === 'V' ? 'win' : x === 'P' ? 'loss' : 'draw'}>{x}</i>)}</span>
const TeamBadge = ({ name, crest }: { name: string; crest?: string | null }) => crest
  ? <img className="team-crest" src={crest} alt="" loading="lazy" onError={event => { event.currentTarget.style.display = 'none' }} />
  : <span className="team-crest crest-fallback" aria-hidden="true">{name.split(/\s+/).slice(0, 2).map(part => part[0]).join('').toUpperCase()}</span>

const poisson = (lambda: number) => {
  const values = [Math.exp(-lambda)]
  for (let goals = 1; goals <= 8; goals++) values.push(values[goals - 1] * lambda / goals)
  const total = values.reduce((sum, value) => sum + value, 0)
  return values.map(value => value / total)
}
const multigolOptions = ['0-1', '0-2', '1-2', '1-3', '1-4', '2-3', '2-4']
const rangeProbability = (mean: number, band: string) => {
  const [low, high] = band.split('-').map(Number)
  return poisson(mean).slice(low, high + 1).reduce((sum, value) => sum + value, 0)
}
const multigolProbability = (match: Match, homeBand: string, awayBand: string) => rangeProbability(Number(match.homeGoals), homeBand) * rangeProbability(Number(match.awayGoals), awayBand)
const multigolCandidates = (match: Match): Pick[] => multigolOptions.flatMap(homeBand => multigolOptions
  .filter(awayBand => {
    const [homeStart, homeEnd] = homeBand.split('-').map(Number)
    const [awayStart, awayEnd] = awayBand.split('-').map(Number)
    return !(homeStart === 0 && awayStart === 0)
      && homeEnd - homeStart <= (homeStart === 1 ? 3 : 2)
      && awayEnd - awayStart <= (awayStart === 1 ? 3 : 2)
  })
  .map(awayBand => ({
    match,
    label: `Multigol casa ${homeBand} + ospite ${awayBand}`,
    probability: multigolProbability(match, homeBand, awayBand),
  })))
const pickForProfile = (match: Match, profile: TicketProfile): Pick => {
  const candidates = multigolCandidates(match).sort((a, b) => b.probability - a.probability)
  const bestProbability = candidates[0]?.probability ?? 0
  const target = profile === 'medium' ? 0.88 : 0.65
  const minimumProbability = profile === 'medium' ? 0.65 : 0.45
  const alternatives = candidates.slice(1).filter(pick => pick.probability >= bestProbability * 0.5)
  const inProfile = alternatives.filter(pick => {
    const ratio = pick.probability / bestProbability
    return pick.probability >= minimumProbability && (profile === 'medium' ? ratio >= 0.75 && ratio <= 0.93 : ratio >= 0.5 && ratio <= 0.75)
  })
  if (!inProfile.length) return candidates[0]
  return inProfile.sort((a, b) => Math.abs(a.probability / bestProbability - target) - Math.abs(b.probability / bestProbability - target))[0]
}

const roundNumber = (round: string) => Number(round.match(/\d+/)?.[0] ?? 999)
const matchKey = (match: Match) => match.id ? String(match.id) : `${match.home}|${match.away}`
const predictedOutcome = (match: Match) => {
  const { home, draw, away } = match.probabilities
  return home >= draw && home >= away ? '1' : draw >= away ? 'X' : '2'
}
const actualOutcome = (match: Match) => (match.homeScore ?? 0) > (match.awayScore ?? 0) ? '1' : match.homeScore === match.awayScore ? 'X' : '2'

export default function App() {
  const [state, setState] = useState<MatchState>({ matches: [], history: [], loading: true, error: null, provider: 'Demo' })
  const [selectedRound, setSelectedRound] = useState('')
  const [selectedHistoryRound, setSelectedHistoryRound] = useState('')
  const [detail, setDetail] = useState<MatchDetails | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState('')
  const [visibleHistoryCount, setVisibleHistoryCount] = useState(12)
  const [ticketSelections, setTicketSelections] = useState<Record<string, string[]>>({})
  const [ticketAddChoices, setTicketAddChoices] = useState<Record<string, string>>({})
  const [copiedTicket, setCopiedTicket] = useState('')

  const apiBase = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '') ?? ''
  useEffect(() => {
    Promise.all([
      fetch(`${apiBase}/api/matches`).then(async response => { if (!response.ok) throw new Error(`Errore API (${response.status})`); return response.json() as Promise<Match[]> }),
      fetch(`${apiBase}/api/health`).then(response => response.ok ? response.json() : { provider: 'API' }).catch(() => ({ provider: 'API' })),
      fetch(`${apiBase}/api/history`).then(response => response.ok ? response.json() as Promise<Match[]> : []).catch(() => [] as Match[]),
    ]).then(([matches, health, history]) => {
      setState({ matches, history, loading: false, error: null, provider: health.provider ?? 'API' })
      const availableRounds = [...new Set(matches.map(match => match.round))].sort((a, b) => roundNumber(a) - roundNumber(b))
      setSelectedRound(current => current && availableRounds.includes(current) ? current : availableRounds[0] ?? '')
    }).catch(() => setState({ matches: [], history: [], loading: false, error: 'Non riesco a raggiungere il backend. Avvia l’API .NET e ricarica la pagina.', provider: 'API' }))
  }, [apiBase])
  useEffect(() => { setTicketSelections({}); setTicketAddChoices({}); setCopiedTicket('') }, [selectedRound])

  const { matches, history, loading, error, provider } = state
  const rounds = useMemo(() => [...new Set(matches.map(match => match.round))].sort((a, b) => roundNumber(a) - roundNumber(b)), [matches])
  const historyRounds = useMemo(() => [...new Set(history.map(match => match.round))].sort((a, b) => roundNumber(a) - roundNumber(b)), [history])
  const visibleMatches = useMemo(() => selectedRound ? matches.filter(match => match.round === selectedRound) : matches, [matches, selectedRound])
  const tickets = useMemo<Ticket[]>(() => {
    if (!selectedRound) return []
    if (!visibleMatches.length) return []
    const counts = [...new Set([Math.min(3, visibleMatches.length), Math.min(5, visibleMatches.length), visibleMatches.length])]
    return (['medium', 'risky'] as const).flatMap(profile => {
      const candidates = visibleMatches.map(match => pickForProfile(match, profile)).sort((a, b) => b.probability - a.probability)
      return counts.map(count => ({ key: `${selectedRound}:${profile}:${count}`, profile, title: count === candidates.length ? 'Tutta la giornata' : `Da ${count} partite`, picks: candidates.slice(0, count) }))
    })
  }, [visibleMatches, selectedRound])
  const historyMatches = useMemo(() => {
    const filtered = selectedHistoryRound ? history.filter(match => match.round === selectedHistoryRound) : history
    return filtered.slice(0, visibleHistoryCount)
  }, [history, selectedHistoryRound, visibleHistoryCount])
  const accuracy = history.filter(match => match.homeScore != null && match.awayScore != null)
  const correctPredictions = accuracy.filter(match => predictedOutcome(match) === actualOutcome(match)).length
  const accuracyPercent = accuracy.length ? Math.round(correctPredictions / accuracy.length * 100) : 0
  const meanGoalError = accuracy.length ? accuracy.reduce((sum, match) => sum + Math.abs(Number(match.homeGoals) - (match.homeScore ?? 0)) + Math.abs(Number(match.awayGoals) - (match.awayScore ?? 0)), 0) / (accuracy.length * 2) : 0
  const brier = accuracy.length ? accuracy.reduce((sum, match) => {
    const actual = actualOutcome(match)
    const probabilities = [match.probabilities.home / 100, match.probabilities.draw / 100, match.probabilities.away / 100]
    const outcomes = [actual === '1' ? 1 : 0, actual === 'X' ? 1 : 0, actual === '2' ? 1 : 0]
    return sum + probabilities.reduce((score, probability, index) => score + (probability - outcomes[index]) ** 2, 0) / 3
  }, 0) / accuracy.length : 0

  const openDetails = async (match: Match) => {
    setDetail(null); setDetailError(''); setDetailLoading(true)
    try {
      const response = await fetch(`${apiBase}/api/matches/${match.id}/details`)
      if (!response.ok) throw new Error('Dettaglio partita non disponibile.')
      setDetail(await response.json() as MatchDetails)
    } catch (error) { setDetailError(error instanceof Error ? error.message : 'Errore nel caricamento del dettaglio.') }
    finally { setDetailLoading(false) }
  }

  const saveTicketPicks = (ticket: Ticket, picks: Pick[]) => setTicketSelections(current => ({ ...current, [ticket.key]: picks.map(pick => matchKey(pick.match)) }))
  const ticketPicks = (ticket: Ticket) => {
    const selectedKeys = ticketSelections[ticket.key] ?? ticket.picks.map(pick => matchKey(pick.match))
    const pool = visibleMatches.map(match => pickForProfile(match, ticket.profile))
    return selectedKeys.map(key => pool.find(pick => matchKey(pick.match) === key)).filter((pick): pick is Pick => Boolean(pick))
  }
  const addTicketMatch = (ticket: Ticket) => {
    const key = ticketAddChoices[ticket.key]
    const match = visibleMatches.find(item => matchKey(item) === key)
    if (!match) return
    const picks = ticketPicks(ticket)
    if (picks.some(pick => matchKey(pick.match) === key)) return
    saveTicketPicks(ticket, [...picks, pickForProfile(match, ticket.profile)])
    setTicketAddChoices(current => ({ ...current, [ticket.key]: '' }))
  }
  const copyTicket = async (ticket: Ticket) => {
    const picks = ticketPicks(ticket)
    const profileName = ticket.profile === 'medium' ? 'Medie · poco rischiose' : 'Rischiose'
    const combinedProbability = picks.length ? picks.reduce((probability, pick) => probability * pick.probability, 1) : 0
    const text = [`${profileName} · ${ticket.title} · ${selectedRound} · 3 € · probabilità combinata stimata ${Math.round(combinedProbability * 100)}%`, ...picks.map((pick, index) => `${index + 1}. ${pick.match.home} - ${pick.match.away}: ${pick.label} (${Math.round(pick.probability * 100)}%)`)].join('\n')
    try { await navigator.clipboard.writeText(text); setCopiedTicket(ticket.key); window.setTimeout(() => setCopiedTicket(''), 1800) }
    catch { setCopiedTicket('') }
  }

  return <main className="shell">
    <header><a className="brand" href="#"><span className="brand-mark">90</span><span>campo<span className="brand-light"> / dati</span></span></a><nav><a className="active" href="#partite">Partite</a><a href="#schedine">Schedine</a><a href="#storico">Storico</a><a href="#metodo">Il modello</a></nav><span className="season">SERIE A · 2026/27</span></header>
    <section className="hero"><div className="eyebrow"><span className="live-dot"/> ANALISI DELLA GIORNATA</div><h1>Una partita alla volta.<br/><em>Con i numeri dalla tua.</em></h1><p>Pronostici basati sulla forma recente, sui gol e sul rendimento casa/trasferta.</p><div className="hero-meta"><span>STAGIONE 2026/27</span><span className="meta-sep"/><span>{matches.length} PARTITE IN ANALISI</span><span className="meta-sep"/><span>{provider.includes('Football-Data.org') ? 'FOOTBALL-DATA.ORG · AZURE SQL' : 'DATI DIMOSTRATIVI'}</span></div></section>
    <section id="partite" className="section-head"><div><span className="eyebrow muted">PROSSIME PARTITE</span><h2>{selectedRound || 'Tutte le giornate'}</h2></div><label className="round-filter">Giornata<select value={selectedRound} onChange={event => setSelectedRound(event.target.value)}><option value="">Tutte</option>{rounds.map(round => <option value={round} key={round}>{round}</option>)}</select></label></section>
    <section className="matches">{loading ? <div className="api-message">Caricamento delle partite…</div> : error ? <div className="api-message error">{error}</div> : visibleMatches.length === 0 ? <div className="api-message">Nessuna partita disponibile per questa giornata.</div> : visibleMatches.map((match, index) => <article className="match" key={matchKey(match)}><div className="match-info"><span className="round">{match.round} <span>·</span> {match.date}</span><div className="teams"><div className="team-side"><TeamBadge name={match.home} crest={match.homeCrest}/><strong>{match.home}</strong></div><span className="versus">—</span><div className="team-side"><TeamBadge name={match.away} crest={match.awayCrest}/><strong>{match.away}</strong></div></div><div className="forms"><span><Form value={match.homeForm}/></span><span className="form-divider"/><span><Form value={match.awayForm}/></span></div></div><div className="prob"><div className="prob-title">PROBABILITÀ 1X2</div><div className="bar"><i style={{width: `${match.probabilities.home}%`}}/><i style={{width: `${match.probabilities.draw}%`}}/><i style={{width: `${match.probabilities.away}%`}}/></div><div className="prob-labels"><span><b>{match.probabilities.home}%</b> 1</span><span><b>{match.probabilities.draw}%</b> X</span><span><b>{match.probabilities.away}%</b> 2</span></div></div><div className="markets"><div className="prob-title">FASCE GOL STIMATE</div><div className="pills"><span><b>{match.bands.home}</b><small>CASA</small></span><span><b>{match.bands.away}</b><small>OSPITE</small></span><span><b>{match.bands.total}</b><small>TOTALE</small></span></div></div><div className="goals"><div className="prob-title">MEDIA GOL / GARA</div><span>{match.homeGoals}<small> casa</small></span><span className="goal-slash">/</span><span>{match.awayGoals}<small> ospite</small></span><button className="details-button" onClick={() => void openDetails(match)}>Dettaglio</button></div><div className="row-num">{String(index + 1).padStart(2, '0')}</div></article>)}</section>

    <section id="schedine" className="tickets-section"><div className="section-head"><div><span className="eyebrow muted">SELEZIONI DAL MODELLO</span><h2>Schedine suggerite · 3 € ciascuna</h2></div></div><p className="ticket-intro">Ogni giornata propone due profili: le schedine medie scelgono alternative vicine alla selezione più probabile; quelle rischiose scelgono alternative meno probabili, ma sempre tra le combinazioni valide. Puoi personalizzare il numero di partite, rimuoverle o aggiungerle. La probabilità complessiva è indicativa e assume indipendenza tra le partite.</p>{!selectedRound ? <div className="api-message">Seleziona una singola giornata per preparare le schedine.</div> : <>{(['medium', 'risky'] as const).map(profile => {
      const profileTickets = tickets.filter(ticket => ticket.profile === profile)
      const profileName = profile === 'medium' ? 'Medie · poco rischiose' : 'Rischiose'
      const profileDescription = profile === 'medium' ? 'Usa alternative vicine alla più probabile solo se raggiungono almeno il 65%; altrimenti sceglie quella con probabilità massima.' : 'Usa alternative al 50–75% della più probabile, con almeno il 45% di probabilità; altrimenti sceglie quella con probabilità massima.'
      return <div className="ticket-profile" key={profile}><div className="ticket-profile-head"><h3>{profileName}</h3><p>{profileDescription}</p></div><div className="ticket-grid">{profileTickets.map(ticket => {
      const picks = ticketPicks(ticket)
      const selectedKeys = new Set(picks.map(pick => matchKey(pick.match)))
      const choices = visibleMatches.filter(match => !selectedKeys.has(matchKey(match)))
      const combinedProbability = picks.length ? picks.reduce((probability, pick) => probability * pick.probability, 1) : 0
      return <article className="ticket" key={ticket.key}><div className="ticket-head"><div><span className="eyebrow muted">{selectedRound.toUpperCase()}</span><h3>{ticket.title}</h3></div><strong>3 €</strong></div><div className="ticket-combined">Probabilità combinata stimata <b>{picks.length ? `${Math.round(combinedProbability * 100)}%` : '—'}</b></div>{picks.map(pick => <div className="ticket-pick" key={matchKey(pick.match)}><span>{pick.match.home} – {pick.match.away}</span><b>{pick.label}</b><small>{Math.round(pick.probability * 100)}% stimata</small><button className="remove-pick" onClick={() => saveTicketPicks(ticket, picks.filter(item => matchKey(item.match) !== matchKey(pick.match)))}>Rimuovi</button></div>)}<div className="ticket-edit"><select aria-label="Aggiungi partita" value={ticketAddChoices[ticket.key] ?? ''} onChange={event => setTicketAddChoices(current => ({ ...current, [ticket.key]: event.target.value }))}><option value="">Aggiungi una partita…</option>{choices.map(match => <option value={matchKey(match)} key={matchKey(match)}>{match.home} – {match.away}</option>)}</select><button onClick={() => addTicketMatch(ticket)} disabled={!ticketAddChoices[ticket.key]}>Aggiungi</button></div><button className="copy-ticket" onClick={() => void copyTicket(ticket)}>{copiedTicket === ticket.key ? 'Copiata' : 'Copia schedina'}</button></article>
    })}</div></div>
    })}</>}</section>

    <section id="storico" className="history-section"><div className="section-head"><div><span className="eyebrow muted">VERIFICA DEL MODELLO</span><h2>Pronostici e risultati passati</h2></div><label className="round-filter">Giornata<select value={selectedHistoryRound} onChange={event => { setSelectedHistoryRound(event.target.value); setVisibleHistoryCount(12) }}><option value="">Tutte</option>{historyRounds.map(round => <option value={round} key={round}>{round}</option>)}</select></label></div><div className="history-metrics"><article><span>PARTITE VALUTATE</span><b>{accuracy.length}</b></article><article><span>ESITO 1X2 INDOVINATO</span><b>{accuracy.length ? `${accuracyPercent}%` : '—'}</b><small>{correctPredictions} su {accuracy.length}</small></article><article><span>SCARTO MEDIO GOL</span><b>{accuracy.length ? meanGoalError.toFixed(2) : '—'}</b><small>gol per squadra</small></article><article><span>BRIER SCORE 1X2</span><b>{accuracy.length ? brier.toFixed(3) : '—'}</b><small>più basso è, meglio è</small></article></div><div className="history-list">{historyMatches.map(match => <article className="history-row" key={matchKey(match)}><span className="history-date">{match.round}<small>{match.date}</small></span><span className="history-team"><TeamBadge name={match.home} crest={match.homeCrest}/>{match.home}</span><b className="history-score">{match.homeScore} – {match.awayScore}</b><span className="history-team"><TeamBadge name={match.away} crest={match.awayCrest}/>{match.away}</span><span className="history-prediction">Stima 1X2: {match.probabilities.home}% / {match.probabilities.draw}% / {match.probabilities.away}%</span><button className="history-detail" onClick={() => void openDetails(match)}>Analisi</button></article>)}{!history.length && <div className="api-message">Lo storico sarà disponibile dopo la sincronizzazione delle partite concluse.</div>}</div>{history.length > historyMatches.length && <button className="more-history" onClick={() => setVisibleHistoryCount(count => count + 12)}>Mostra altre partite</button>}</section>

    <section id="metodo" className="method"><div><span className="eyebrow muted">COME LEGGERE I DATI</span><h2>Stime, non certezze.</h2></div><p>Il modello usa i risultati disponibili delle ultime 5 partite per squadra e una distribuzione di Poisson sui gol attesi. Le schedine sono combinazioni automatiche di mercati con probabilità stimate: non sono quote né garanzie di vincita. L’archivio ricostruisce le previsioni usando soltanto risultati precedenti alla partita, per limitare distorsioni nella verifica.</p></section>
    <footer><span>campo / dati <small>Un progetto indipendente</small></span><span>Le previsioni hanno finalità informative.<br/><a href="https://www.football-data.org/" target="_blank" rel="noreferrer">Football data provided by the Football-Data.org API</a></span><span>PROTOTIPO 0.1</span></footer>

    {(detailLoading || detailError || detail) && <div className="modal-backdrop" role="presentation" onClick={() => { setDetail(null); setDetailError(''); setDetailLoading(false) }}><section className="match-modal" role="dialog" aria-modal="true" aria-label="Dettaglio partita" onClick={event => event.stopPropagation()}><button className="modal-close" onClick={() => { setDetail(null); setDetailError(''); setDetailLoading(false) }} aria-label="Chiudi">×</button>{detailLoading ? <div className="api-message">Caricamento analisi…</div> : detailError ? <div className="api-message error">{detailError}</div> : detail && <><span className="eyebrow muted">{detail.match.round} · {detail.match.date}</span><div className="modal-teams"><TeamBadge name={detail.match.home} crest={detail.match.homeCrest}/><h2>{detail.match.home}</h2>{detail.match.homeScore != null && <b>{detail.match.homeScore} – {detail.match.awayScore}</b>}<h2>{detail.match.away}</h2><TeamBadge name={detail.match.away} crest={detail.match.awayCrest}/></div><div className="modal-metrics"><article><span>PROBABILITÀ 1X2</span><b>{detail.match.probabilities.home}% · {detail.match.probabilities.draw}% · {detail.match.probabilities.away}%</b></article><article><span>GOL ATTESI</span><b>{detail.match.homeGoals} casa · {detail.match.awayGoals} ospite</b></article><article><span>FASCE STIMATE</span><b>{detail.match.bands.home} casa · {detail.match.bands.away} ospite · {detail.match.bands.total} totale</b></article></div><div className="recent-columns">{([['Ultime partite casa', detail.homeRecent], ['Ultime partite ospite', detail.awayRecent]] as const).map(([title, recent]) => <div className="recent-column" key={title}><h3>{title}</h3>{recent.length ? recent.map((item, index) => <div className="recent-row" key={`${item.date}-${item.opponent}-${index}`}><span>{item.atHome ? 'C' : 'T'}</span><span>{item.opponent}</span><small>{item.goalsFor}–{item.goalsAgainst}</small><small>{item.date}</small></div>) : <p>Dati recenti non disponibili.</p>}</div>)}</div></>}</section></div>}
  </main>
}
