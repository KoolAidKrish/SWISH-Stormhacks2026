import { useEffect, useRef, useState, type FormEvent } from 'react'

type Key = 'W' | 'A' | 'S' | 'D' | 'SHIFT' | 'SPACE' | 'LMB' | 'RMB' | 'CTRL' | 'ALT' | 'J'
type Seg = { key: Key; start: number; end: number }
type Cmd = {
  say: string
  steps: string[]
  segs: Seg[]
  latch?: Key
  release?: boolean
}

// Durations come straight from src/VoiceKeys/presets/rocket-league.json:
// left = 350 ms; "hard" ×2.2; "little" ×0.35; "really" raises the next scale to the 1.7th power.
const hard = 2.2
const little = 0.35
const really = Math.pow(hard, 1.7)
const ms = (n: number) => Math.round(n)

const COMMANDS: Cmd[] = [
  { say: 'drive', steps: ['unlatch s', 'latch w'], segs: [], latch: 'W' },
  { say: 'left', steps: ['hold a 350'], segs: [{ key: 'A', start: 0, end: 350 }] },
  { say: 'hard left', steps: [`hold a ${ms(350 * hard)}`], segs: [{ key: 'A', start: 0, end: ms(350 * hard) }] },
  { say: 'left a little', steps: [`hold a ${ms(350 * little)}`], segs: [{ key: 'A', start: 0, end: ms(350 * little) }] },
  { say: 'really hard right', steps: [`hold d ${ms(350 * really)}`], segs: [{ key: 'D', start: 0, end: ms(350 * really) }] },
  { say: 'boost', steps: ['hold lmb 1500'], segs: [{ key: 'LMB', start: 0, end: 1500 }] },
  { say: 'little boost', steps: [`hold lmb ${ms(1500 * little)}`], segs: [{ key: 'LMB', start: 0, end: ms(1500 * little) }] },
  { say: 'jump', steps: ['tap rmb 60'], segs: [{ key: 'RMB', start: 0, end: 60 }] },
  {
    say: 'double jump', steps: ['tap rmb 60', 'wait 90', 'tap rmb 60'],
    segs: [{ key: 'RMB', start: 0, end: 60 }, { key: 'RMB', start: 150, end: 210 }],
  },
  { say: 'powerslide', steps: ['hold shift 500'], segs: [{ key: 'SHIFT', start: 0, end: 500 }] },
  {
    say: 'turn around', steps: ['down shift', 'hold a 900', 'up shift'],
    segs: [{ key: 'SHIFT', start: 0, end: 900 }, { key: 'A', start: 0, end: 900 }],
  },
  // plain "keys" taps are held for keyHoldMs (35 ms, settings.json)
  { say: 'ball cam', steps: ['tap space'], segs: [{ key: 'SPACE', start: 0, end: 35 }] },
  // gestures.json holds these 150 ms so a camera frame is sure to see the hotkey
  {
    say: 'mouse mode', steps: ['hold ctrl+alt+j 150'],
    segs: [{ key: 'CTRL', start: 0, end: 150 }, { key: 'ALT', start: 0, end: 150 }, { key: 'J', start: 0, end: 150 }],
  },
  { say: 'stop', steps: ['release'], segs: [], release: true },
]

const KEY_ROWS: Key[][] = [
  ['W', 'A', 'S', 'D'],
  ['SHIFT', 'SPACE', 'LMB', 'RMB'],
  ['CTRL', 'ALT', 'J'],
]
const SCALE = 1500 // ms shown across the timeline
const norm = (s: string) => s.toLowerCase().replace(/[^a-z ]/g, '').replace(/\s+/g, ' ').trim()

export default function VoiceDemo() {
  const [cmd, setCmd] = useState<Cmd | null>(null)
  const [missed, setMissed] = useState<string | null>(null)
  const [typed, setTyped] = useState('')
  const [t, setT] = useState(-1)
  const [latched, setLatched] = useState<Set<Key>>(new Set())
  const [input, setInput] = useState('')
  const raf = useRef(0)
  const timers = useRef<number[]>([])

  useEffect(() => () => {
    cancelAnimationFrame(raf.current)
    timers.current.forEach(clearTimeout)
  }, [])

  const run = (c: Cmd) => {
    cancelAnimationFrame(raf.current)
    timers.current.forEach(clearTimeout)
    timers.current = []
    setMissed(null)
    setCmd(c)
    setT(-1)
    setTyped('')
    // the transcript arrives a character at a time, then the keys fire
    const chars = c.say.length
    for (let i = 1; i <= chars; i++) {
      timers.current.push(window.setTimeout(() => setTyped(c.say.slice(0, i)), i * 28))
    }
    timers.current.push(window.setTimeout(() => {
      if (c.latch) setLatched(prev => new Set(prev).add(c.latch!))
      if (c.release) setLatched(new Set())
      const end = Math.max(300, ...c.segs.map(s => s.end)) + 250
      const t0 = performance.now()
      const step = () => {
        const now = performance.now() - t0
        setT(Math.min(now, end))
        if (now < end) raf.current = requestAnimationFrame(step)
      }
      raf.current = requestAnimationFrame(step)
    }, chars * 28 + 160))
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const n = norm(input)
    if (!n) return
    const hit = COMMANDS.find(c => c.say === n)
    if (hit) run(hit)
    else {
      cancelAnimationFrame(raf.current)
      setCmd(null)
      setT(-1)
      setTyped(input.trim())
      setMissed(input.trim())
    }
    setInput('')
  }

  const active = new Set<Key>(latched)
  if (cmd && t >= 0) for (const s of cmd.segs) if (t >= s.start && t < s.end) active.add(s.key)

  return (
    <div className="demo" data-reveal>
      <div className="demo-left">
        <p className="demo-label">Rocket League preset: tap a phrase</p>
        <div className="chips">
          {COMMANDS.map(c => (
            <button key={c.say} className={`chip ${cmd?.say === c.say ? 'on' : ''}`} onClick={() => run(c)}>
              "{c.say}"
            </button>
          ))}
        </div>
        <form className="say-form" onSubmit={submit}>
          <label htmlFor="say" className="demo-label">or type what you'd say</label>
          <div className="say-row">
            <input id="say" value={input} onChange={e => setInput(e.target.value)} placeholder="hard left" autoComplete="off" />
            <button className="btn btn-solid" type="submit">Say</button>
          </div>
        </form>
      </div>

      <div className="demo-right">
        <div className="term">
          <div className="term-bar"><i /><i /><i /><span>VoiceEngine</span></div>
          <div className="term-body">
            <p><span className="dim">heard ›</span> <span className="heard">{typed}</span><span className="caret" /></p>
            {cmd && t >= 0 && (
              <p className="prog">
                <span className="dim">do ›</span> {cmd.steps.join(', ')}
              </p>
            )}
            {missed && (
              <p className="miss">
                <span className="dim">no match ›</span> normal speech doesn't press keys. Every word has to be part of a command, a modifier or a filler word.
              </p>
            )}
            {!typed && !missed && <p className="dim">waiting for speech…</p>}
          </div>
        </div>

        <div className="keys" aria-live="polite">
          {KEY_ROWS.map((row, r) => (
            <div className="key-row" key={r}>
              {row.map(k => (
                <span key={k} className={`key ${active.has(k) ? 'down' : ''} ${latched.has(k) ? 'latched' : ''} k-${k.toLowerCase()}`}>
                  {k}
                  {latched.has(k) && <em>latched</em>}
                </span>
              ))}
            </div>
          ))}
        </div>

        <div className="timeline" aria-hidden="true">
          {cmd?.segs.map((s, i) => (
            <div key={i} className="seg-row">
              <span className="seg-key">{s.key}</span>
              <div className="seg-track">
                <div
                  className="seg"
                  style={{
                    left: `${Math.min(100, (s.start / SCALE) * 100)}%`,
                    width: `${Math.max(0.6, (Math.min(Math.max(t, s.start), s.end) - s.start) / SCALE * 100)}%`,
                    opacity: t >= s.start ? 1 : 0,
                  }}
                />
                {t >= s.end && <span className="seg-ms" style={{ left: `${Math.min(86, (s.end / SCALE) * 100)}%` }}>{s.end - s.start} ms</span>}
              </div>
            </div>
          ))}
          {cmd && cmd.segs.length === 0 && t >= 0 && (
            <p className="dim small">{cmd.latch ? `${cmd.latch} stays down until you say "stop".` : 'All latched keys let go.'}</p>
          )}
          <div className="ruler"><span>0</span><span>500</span><span>1000</span><span>1500 ms</span></div>
        </div>
      </div>
    </div>
  )
}
