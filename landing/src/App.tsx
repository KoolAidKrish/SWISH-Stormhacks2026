import { useEffect, useRef, useState, type CSSProperties } from 'react'
import Hero from './Hero'
import HeroCircuit from './HeroCircuit'
import VoiceDemo from './VoiceDemo'
import FingerKeys from './FingerKeys'

const v = (o: Record<string, string | number>) => o as CSSProperties

const VERSIONS = [
  { id: 1, theme: 'slate', name: 'Slate' },
  { id: 2, theme: 'circuit', name: 'Circuit' },
  { id: 3, theme: 'neon', name: 'Neon' },
] as const
type VersionId = (typeof VERSIONS)[number]['id']
const readVersion = (): VersionId => {
  const n = Number(new URLSearchParams(location.search).get('v'))
  return VERSIONS.find(x => x.id === n)?.id ?? 1
}

// 21 MediaPipe-style landmarks for an open right hand, in a 200×220 box
const LM: [number, number][] = [
  [100, 196], [70, 176], [50, 152], [38, 128], [28, 106],
  [80, 112], [76, 80], [74, 58], [73, 38],
  [101, 106], [101, 70], [101, 46], [101, 24],
  [121, 110], [125, 78], [127, 56], [129, 38],
  [139, 122], [148, 98], [153, 82], [157, 66],
]
const BONES = [
  [0, 1], [1, 2], [2, 3], [3, 4], [0, 5], [5, 6], [6, 7], [7, 8], [5, 9], [9, 10], [10, 11], [11, 12],
  [9, 13], [13, 14], [14, 15], [15, 16], [13, 17], [17, 18], [18, 19], [19, 20], [0, 17],
]

const FEATURES = [
  { tag: '01', title: 'Pinch to click', body: 'Your right palm moves the cursor. Pinch your fingers to click. Make a fist to lift the mouse and reposition, like picking one up off the desk.' },
  { tag: '02', title: 'Fold a finger, hold a key', body: 'Your left hand is a WASD pad: thumb Space, index D, middle W, ring A, pinky S. Fold two fingers and both keys hold, so you can run and strafe at once.' },
  { tag: '03', title: 'Three mouse modes', body: 'Relative for aiming in games, Absolute for pointing at the screen, Joystick for steering. Switch with Ctrl+Alt+J or just say "mouse mode".' },
  { tag: '04', title: 'Commands that read like English', body: '"left" holds A for 350 ms, "hard left" 2.2× longer, "left a little" about a third. Presets for Desktop, Rocket League and Minecraft swap live when you say "minecraft mode".' },
  { tag: '05', title: 'Follow-the-dot calibration', body: 'A dot snakes across the screen for ~30 s and you trace it with your palm. It measures how far your hand lags, drops glitchy frames, and refuses to save a bad run.' },
  { tag: '06', title: 'Nothing gets stuck', body: 'A left-hand fist is a rest pose that presses nothing, and every key lets go the moment your hand leaves the frame. No camera? Voice still works. No API key? Your hands still drive.' },
]

const HOTKEYS = [
  { keys: ['Ctrl', 'Alt', 'M'], label: 'hand mouse on / off' },
  { keys: ['Ctrl', 'Alt', 'K'], label: 'finger keyboard on / off' },
  { keys: ['Ctrl', 'Alt', 'J'], label: 'cycle mouse mode' },
  { keys: ['Ctrl', 'Alt', 'V'], label: 'show / hide window' },
  { keys: ['Ctrl', 'Alt', 'Q'], label: 'quit' },
]

function useReveal() {
  useEffect(() => {
    const els = document.querySelectorAll<HTMLElement>('[data-reveal]')
    const io = new IntersectionObserver(
      entries => entries.forEach(e => {
        if (e.isIntersecting) {
          e.target.classList.add('in')
          io.unobserve(e.target)
        }
      }),
      { threshold: 0.15, rootMargin: '0px 0px -8% 0px' },
    )
    els.forEach(el => io.observe(el))
    return () => io.disconnect()
  }, [])
}

/** A ring that glides after the pointer, the way the hand mouse smooths your palm. */
function GlideCursor() {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!matchMedia('(pointer: fine)').matches || matchMedia('(prefers-reduced-motion: reduce)').matches) return
    const el = ref.current!
    let x = innerWidth / 2, y = innerHeight / 2, tx = x, ty = y, raf = 0
    const move = (e: PointerEvent) => {
      tx = e.clientX
      ty = e.clientY
      el.style.opacity = '1'
      const t = e.target as HTMLElement
      el.classList.toggle('hot', !!t.closest('a,button,input,.fk-hit'))
    }
    const tick = () => {
      x += (tx - x) * 0.18
      y += (ty - y) * 0.18
      el.style.transform = `translate(${x}px, ${y}px)`
      raf = requestAnimationFrame(tick)
    }
    const down = () => el.classList.add('pinch')
    const up = () => el.classList.remove('pinch')
    addEventListener('pointermove', move)
    addEventListener('pointerdown', down)
    addEventListener('pointerup', up)
    raf = requestAnimationFrame(tick)
    return () => {
      removeEventListener('pointermove', move)
      removeEventListener('pointerdown', down)
      removeEventListener('pointerup', up)
      cancelAnimationFrame(raf)
    }
  }, [])
  return <div className="glide" ref={ref} aria-hidden="true"><span /></div>
}

function Nav({ version, onVersion }: { version: VersionId; onVersion: (id: VersionId) => void }) {
  const [scrolled, setScrolled] = useState(false)
  const bar = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const on = () => {
      setScrolled(scrollY > 40)
      const max = document.documentElement.scrollHeight - innerHeight
      bar.current?.style.setProperty('transform', `scaleX(${max > 0 ? scrollY / max : 0})`)
    }
    on()
    addEventListener('scroll', on, { passive: true })
    return () => removeEventListener('scroll', on)
  }, [])
  return (
    <nav className={`nav ${scrolled ? 'solid' : ''}`}>
      <a href="#top" className="nav-logo">SWISH<span>/</span></a>
      <div className="nav-links">
        <a href="#how">How it works</a>
        <a href="#left-hand">Left hand</a>
        <a href="#demo">Voice</a>
        <a href="https://github.com/KoolAidKrish/SWISH-Stormhacks2026" target="_blank" rel="noreferrer">GitHub ↗</a>
      </div>
      <div className="vswitch" role="group" aria-label="Page version">
        {VERSIONS.map(x => (
          <button key={x.id} className={x.id === version ? 'on' : ''} aria-pressed={x.id === version} onClick={() => onVersion(x.id)} title={x.name}>
            V{x.id}<span> {x.name}</span>
          </button>
        ))}
      </div>
      <div className="nav-progress" ref={bar} />
    </nav>
  )
}

function HandCard() {
  return (
    <article className="channel" data-reveal>
      <div className="channel-art hand-art" aria-hidden="true">
        <div className="cam-frame">
          <span className="rec">● CAM</span>
          <svg viewBox="0 0 200 220" className="hand-svg">
            <g className="hand-move">
              {BONES.map(([a, b], i) => (
                <line key={i} x1={LM[a][0]} y1={LM[a][1]} x2={LM[b][0]} y2={LM[b][1]} />
              ))}
              {LM.map(([x, y], i) => (
                <circle key={i} cx={x} cy={y} r={i % 4 === 0 && i ? 4.5 : 3.5} style={v({ '--i': i })} />
              ))}
            </g>
          </svg>
        </div>
        <div className="arrow-flow"><i /><i /><i /></div>
        <div className="screen-frame">
          <svg viewBox="0 0 100 80" className="screen-svg">
            <path className="trail" d="M18 58 C 30 20, 55 20, 62 40 S 85 64, 82 24" pathLength={1} />
            <g className="cursor-move">
              <circle className="ripple" r="6" />
              <path className="cursor" d="M0 0 L0 13 L3.5 9.8 L6 15 L8.2 14 L5.8 9 L10.5 9 Z" />
            </g>
          </svg>
        </div>
      </div>
      <p className="channel-kicker">Channel 01 · Right hand</p>
      <h3>Your right hand is the mouse.</h3>
      <p>
        A palm detector and a hand-landmark model track both hands from any webcam. Move your right palm and the
        cursor glides after it, smoothed so it doesn't jitter. Pinch to click. No glove, no sensor.
      </p>
    </article>
  )
}

function LeftHandCard() {
  const keys = [['Space', 'thumb'], ['D', 'index'], ['W', 'middle'], ['A', 'ring'], ['S', 'pinky']]
  return (
    <article className="channel" data-reveal style={v({ '--delay': '120ms' })}>
      <div className="channel-art left-art" aria-hidden="true">
        {keys.map(([k, f], i) => (
          <div className="lk" key={k} style={v({ '--i': i })}>
            <span className="lk-finger"><i /></span>
            <kbd>{k}</kbd>
            <small>{f}</small>
          </div>
        ))}
      </div>
      <p className="channel-kicker">Channel 02 · Left hand</p>
      <h3>Your left hand is the keyboard.</h3>
      <p>
        Fold a finger and its key goes down; straighten it and the key comes up. Hold W with your middle finger while
        your right hand steers, the same split as a keyboard and mouse.
      </p>
    </article>
  )
}

function VoiceCard() {
  return (
    <article className="channel" data-reveal style={v({ '--delay': '240ms' })}>
      <div className="channel-art voice-art" aria-hidden="true">
        <div className="wave">
          {Array.from({ length: 28 }, (_, i) => (
            <i key={i} style={v({ '--i': i, '--h': 0.25 + Math.abs(Math.sin(i * 1.7)) * 0.75 })} />
          ))}
        </div>
        <div className="voice-lines">
          <p><span>"hard left"</span><b>A · 770 ms</b></p>
          <p><span>"boost"</span><b>LMB · 1500 ms</b></p>
          <p><span>"double jump"</span><b>RMB · RMB</b></p>
        </div>
      </div>
      <p className="channel-kicker">Channel 03 · Voice</p>
      <h3>Your voice does the rest.</h3>
      <p>
        Speech streams to ElevenLabs realtime speech-to-text and is matched against the active preset: taps, holds,
        latches, whole flip combos. Your hands handle the fast stuff; voice handles everything else.
      </p>
    </article>
  )
}

function Pipeline() {
  const right = ['Camera', 'Palm + landmark models', 'HandMouse glide', 'Mouse']
  const left = ['Camera', 'Palm + landmark models', 'HandKeyboard', 'WASD + Space']
  const voice = ['Microphone', 'ElevenLabs Scribe', 'Preset matcher', 'Keystrokes']
  const row = (label: string, items: string[], k: number) => (
    <div className="pipe-row" data-reveal style={v({ '--delay': `${k * 140}ms` })}>
      <span className="pipe-label">{label}</span>
      <div className="pipe-nodes">
        {items.map((n, i) => (
          <div className="pipe-step" key={n} style={v({ '--i': i })}>
            <div className="pipe-node">{n}</div>
            {i < items.length - 1 && <div className="pipe-link"><i /></div>}
          </div>
        ))}
      </div>
    </div>
  )
  return (
    <div className="pipeline">
      <div className="pipe-app" data-reveal>
        <span className="pipe-app-name">SWISH.App</span>
        <span className="pipe-app-sub">WPF window + tray · both engines, one process</span>
      </div>
      {row('Gesture · right hand', right, 0)}
      {row('Gesture · left hand', left, 1)}
      {row('VoiceEngine', voice, 2)}
    </div>
  )
}

export default function App() {
  useReveal()
  const [version, setVersion] = useState<VersionId>(readVersion)
  useEffect(() => {
    document.documentElement.dataset.theme = VERSIONS.find(x => x.id === version)!.theme
  }, [version])
  const switchVersion = (id: VersionId) => {
    setVersion(id)
    const url = new URL(location.href)
    url.searchParams.set('v', String(id))
    history.replaceState(null, '', url)
  }

  return (
    <>
      <GlideCursor />
      <Nav version={version} onVersion={switchVersion} />
      {version === 2 ? <HeroCircuit key="circuit" /> : <Hero key={version} />}

      <div className="marquee" aria-hidden="true">
        <div className="marquee-track">
          {Array.from({ length: 2 }, (_, k) => (
            <span key={k}>
              RIGHT HAND → MOUSE <b>✕</b> LEFT HAND → KEYS <b>✕</b> VOICE → COMMANDS <b>✕</b> NO CONTROLLER <b>✕</b> ANY WEBCAM <b>✕</b>{' '}
            </span>
          ))}
        </div>
      </div>

      <main>
        <section id="how" className="section">
          <div className="section-head" data-reveal>
            <p className="eyebrow">// how it works</p>
            <h2>Two hands. One voice.<br /><span className="hl">Zero controllers.</span></h2>
            <p className="lede">
              SWISH turns a webcam and a microphone into a full game controller. The camera watches both hands at once:
              the right one is your mouse, the left one is your keyboard. Your voice covers everything the hands don't.
            </p>
          </div>
          <div className="channels">
            <HandCard />
            <LeftHandCard />
            <VoiceCard />
          </div>
        </section>

        <section id="left-hand" className="section section-alt">
          <div className="section-head" data-reveal>
            <p className="eyebrow">// left hand</p>
            <h2>Fold a finger.<br /><span className="hl">Hold a key.</span></h2>
            <p className="lede">
              The left hand is read finger by finger: each fingertip's distance from the wrist decides whether it's
              folded, with a gap between press and release so keys don't flicker. Try it below.
            </p>
          </div>
          <FingerKeys />
        </section>

        <section className="section">
          <div className="section-head" data-reveal>
            <p className="eyebrow">// under the hood</p>
            <h2>Two engines, <span className="hl">one app.</span></h2>
            <p className="lede">
              One camera loop splits the hands, right to the mouse and left to the keyboard. Voice runs alongside it in
              the same tray app, and each engine still runs on its own as a console tool.
            </p>
          </div>
          <Pipeline />
        </section>

        <section id="demo" className="section section-alt">
          <div className="section-head" data-reveal>
            <p className="eyebrow">// voice</p>
            <h2>Say it. <span className="hl">It presses it.</span></h2>
            <p className="lede">
              These are the real Rocket League commands and timings from the preset file. Modifiers scale the hold time
              the way English reads.
            </p>
          </div>
          <VoiceDemo />
        </section>

        <section id="features" className="section">
          <div className="section-head" data-reveal>
            <p className="eyebrow">// features</p>
            <h2>Built to <span className="hl">actually play.</span></h2>
          </div>
          <div className="features">
            {FEATURES.map((f, i) => (
              <article className="feature" key={f.tag} data-reveal style={v({ '--delay': `${(i % 3) * 90}ms` })}>
                <span className="feature-tag">{f.tag}</span>
                <h3>{f.title}</h3>
                <p>{f.body}</p>
                <i className="feature-corner" aria-hidden="true" />
              </article>
            ))}
          </div>

          <div className="hotkeys" data-reveal>
            {HOTKEYS.map(h => (
              <div className="hotkey" key={h.label}>
                <span className="combo">
                  {h.keys.map((k, i) => <span key={k}>{i > 0 && <b>+</b>}<kbd>{k}</kbd></span>)}
                </span>
                <span className="hotkey-label">{h.label}</span>
              </div>
            ))}
          </div>
        </section>

        <section className="cta">
          <div className="cta-stripes" aria-hidden="true" />
          <div className="cta-panel" data-reveal>
            <p className="eyebrow">// stormhacks 2026 · simon fraser university</p>
            <h2>Put the controller down.</h2>
            <p className="lede">
              SWISH was built at StormHacks 2026. The code is on GitHub: grab it, add a preset for your game, and play
              with nothing in your hands.
            </p>
            <div className="hero-ctas">
              <a className="btn btn-solid" href="https://github.com/KoolAidKrish/SWISH-Stormhacks2026" target="_blank" rel="noreferrer">Get SWISH on GitHub ↗</a>
              <a className="btn btn-ghost" href="#left-hand">Try the demos again</a>
            </div>
          </div>
        </section>
      </main>

      <footer className="footer">
        <span className="nav-logo">SWISH<span>/</span></span>
        <span>Serial · Wireless · Interactive · System · for · Humans</span>
        <span>Speech by ElevenLabs</span>
      </footer>
    </>
  )
}
