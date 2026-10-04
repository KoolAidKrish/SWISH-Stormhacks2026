import { useState, type CSSProperties, type ReactNode } from 'react'
import { SHOW_EMPTY_SLOTS, buildLog, demoVideo, gallery, links, stats, story, team } from './content'

const v = (o: Record<string, string | number>) => o as CSSProperties

/** A dashed placeholder that names the content.ts field to fill. Hidden when SHOW_EMPTY_SLOTS is off. */
export function Slot({ field, hint, className = '', children }: { field: string; hint?: string; className?: string; children?: ReactNode }) {
  if (!SHOW_EMPTY_SLOTS) return null
  return (
    <div className={`slot ${className}`}>
      <span className="slot-tag">FILL IN</span>
      {children}
      <code>content.ts → {field}</code>
      {hint && <p>{hint}</p>}
    </div>
  )
}

function Head({ eyebrow, title, accent, lede }: { eyebrow: string; title: ReactNode; accent?: ReactNode; lede?: ReactNode }) {
  return (
    <div className="section-head" data-reveal>
      <p className="eyebrow">// {eyebrow}</p>
      <h2>{title}{accent && <> <span className="hl">{accent}</span></>}</h2>
      {lede && <p className="lede">{lede}</p>}
    </div>
  )
}

/* ---------- demo video ---------- */
export function VideoSection() {
  const { youtubeId, mp4, poster, caption } = demoVideo
  const has = youtubeId || mp4
  return (
    <section id="video" className="section">
      <Head eyebrow="see it work" title="Look Ma!" accent="No mouse!" />
      <div className="video-frame" data-reveal>
        <span className="vf-corner tl" /><span className="vf-corner br" />
        {youtubeId ? (
          <iframe
            src={`https://www.youtube-nocookie.com/embed/${youtubeId}?rel=0`}
            title="SWISH demo video"
            allow="accelerometer; encrypted-media; gyroscope; picture-in-picture; fullscreen"
            allowFullScreen
          />
        ) : mp4 ? (
          <video src={mp4} poster={poster || undefined} controls playsInline preload="metadata" />
        ) : (
          <Slot field="demoVideo.youtubeId  or  demoVideo.mp4" hint="30–60 s of someone playing with SWISH. Show both hands and a voice command." className="slot-video">
            <span className="play-glyph" aria-hidden="true" />
          </Slot>
        )}
      </div>
      {caption ? <p className="video-caption" data-reveal>{caption}</p> : has && <Slot field="demoVideo.caption" className="slot-line" />}
    </section>
  )
}

/* ---------- stats ---------- */
export function Stats() {
  return (
    <div className="stats" data-reveal>
      {stats.map((s, i) =>
        s.value ? (
          <div className="stat" key={i} style={v({ '--i': i })}>
            <b>{s.value}</b>
            <span>{s.label}</span>
          </div>
        ) : (
          <Slot key={i} field={`stats[${i}]`} hint="A number you measured, and what it means." className="stat slot-stat" />
        ),
      )}
    </div>
  )
}

/* ---------- presets ---------- */
const PRESETS = [
  {
    name: 'Desktop',
    say: 'desktop mode',
    blurb: 'Hands-free browsing and media. The default preset.',
    cmds: ['"new tab"', '"close tab"', '"go back"', '"page down"', '"volume up"', '"play" / "pause"', '"screenshot"', '"switch window"'],
  },
  {
    name: 'Minecraft',
    say: 'minecraft mode',
    blurb: 'Your right hand aims the camera; voice walks, mines and manages the hotbar.',
    cmds: ['"walk"', '"sneak"', '"keep mining"', '"place"', '"eat"', '"slot three"', '"inventory"', '"a little forward"'],
  },
  {
    name: 'Aimlabs',
    say: 'aimlabs mode',
    blurb: 'Almost all mouse: your hand aims and a pinch shoots. Keys follow the usual shooter layout.',
    cmds: ['"shoot"', '"hold fire"', '"stop"', '"scope"', '"reload"', '"strafe left"', '"jump"', '"crouch"'],
  },
  {
    name: 'Rocket League',
    say: 'rocket league mode',
    blurb: 'Driving, boost and every flip as a single phrase. Hold times scale with how you say it.',
    cmds: ['"drive"', '"hard left"', '"boost"', '"double jump"', '"flip left"', '"powerslide"', '"ball cam"', '"stop"'],
  },
  {
    name: 'Bloons TD 6',
    say: 'bloons mode',
    blurb: 'Pick a tower by voice, then place it with a pinch where your hand points.',
    cmds: ['"dart monkey"', '"ninja"', '"banana farm"', '"hero"', '"upgrade top"', '"sell"', '"target"', '"fast forward"'],
  },
]

export function Presets() {
  return (
    <section id="presets" className="section">
      <Head
        eyebrow="presets"
        title="One voice,"
        accent="every game."
        lede={<>A preset is a JSON file of phrases and the keys they press. Switch out loud, mid-game, and SWISH announces the new mode in an ElevenLabs voice.</>}
      />
      <div className="presets">
        {PRESETS.map((p, i) => (
          <article className="preset" key={p.name} data-reveal style={v({ '--delay': `${i * 90}ms` })}>
            <header>
              <h3>{p.name}</h3>
              <span className="preset-say">say "{p.say}"</span>
            </header>
            <p>{p.blurb}</p>
            <ul>{p.cmds.map(c => <li key={c}>{c}</li>)}</ul>
          </article>
        ))}
        <article className="preset preset-new" data-reveal style={v({ '--delay': '270ms' })}>
          <header>
            <h3>Your game</h3>
            <span className="preset-say">say "… mode"</span>
          </header>
          <p>Copy a preset, change the phrases and keys, and drop it in <code>src/VoiceKeys/presets/</code>; it answers to its own switch phrase. Or skip the JSON: the in-app command editor adds voice or gesture commands that work in every game.</p>
          <pre>{`{ "say": [ "reload" ],
  "keys": [ "r" ] }`}</pre>
        </article>
      </div>
    </section>
  )
}

/* ---------- ElevenLabs ---------- */
export function ElevenLabs() {
  const items = [
    { k: 'Scribe realtime', t: 'Speech-to-text over a WebSocket', b: 'Audio streams to scribe_v2_realtime as you speak. With the open mic (the default), a command fires as soon as it\'s certain, even mid-sentence, or after a 0.3 s pause. Push-to-talk is optional.' },
    { k: 'Keyterms', t: 'Biased toward game words', b: 'Words from the active preset are sent as keyterms (up to 50) to steer recognition toward what you\'ll actually say. Switching preset reconnects with the new list.' },
    { k: 'Flash TTS', t: 'It talks back', b: 'Mode switches and confirmations are spoken with eleven_flash_v2_5, generated once and cached on disk. Pick the voice and volume in Settings.' },
  ]
  return (
    <section id="elevenlabs" className="section section-alt">
      <Head eyebrow="powered by elevenlabs" title="Fast enough" accent="to play with." />
      <div className="el-grid">
        {items.map((it, i) => (
          <article className="el-card" key={it.k} data-reveal style={v({ '--delay': `${i * 100}ms` })}>
            <span className="el-k">{it.k}</span>
            <h3>{it.t}</h3>
            <p>{it.b}</p>
          </article>
        ))}
      </div>
      <div className="el-flow" data-reveal aria-hidden="true">
        <span>mic</span><i /><span>scribe</span><i /><span>matcher</span><i /><span>keys</span><i /><span>voice reply</span>
      </div>
    </section>
  )
}

/* ---------- gallery ---------- */
export function Gallery() {
  if (!SHOW_EMPTY_SLOTS && !gallery.some(g => g.src)) return null
  return (
    <section id="gallery" className="section">
      <Head eyebrow="from the weekend" title="Screens" accent="& scenes." />
      <div className="gallery">
        {gallery.map((g, i) => (
          <figure key={i} className={`shot ${i === 0 ? 'wide' : ''}`} data-reveal style={v({ '--delay': `${i * 90}ms` })}>
            {g.src ? <img src={g.src} alt={g.caption} loading="lazy" /> : <Slot field={`gallery[${i}].src`} className="slot-shot" />}
            {g.caption && <figcaption>{g.caption}</figcaption>}
          </figure>
        ))}
      </div>
    </section>
  )
}

/* ---------- story + build log ---------- */
export function Story() {
  return (
    <section id="story" className="section section-alt">
      <Head eyebrow="the story" title="Built in" accent="one hackathon." />
      <div className="story">
        <div className="story-why" data-reveal>
          <h3>Why we built it</h3>
          {story.why ? <p>{story.why}</p> : <Slot field="story.why" hint="2–4 sentences: the moment that made you want to play without a controller." />}
          {story.quote ? (
            <blockquote>
              <p>“{story.quote}”</p>
              {story.quoteBy && <cite>— {story.quoteBy}</cite>}
            </blockquote>
          ) : (
            <Slot field="story.quote / story.quoteBy" hint="Optional: something a tester or judge said." className="slot-line" />
          )}
          <p className="story-access">
            Playing without gripping anything could also matter to people for whom a controller is hard to hold. We
            haven't tested that with users yet, but it's where we'd like to take SWISH next.
          </p>
        </div>
        <ol className="log">
          {buildLog.map((e, i) => (
            <li key={i} data-reveal style={v({ '--delay': `${i * 70}ms` })}>
              <span className="log-when">{e.when || (SHOW_EMPTY_SLOTS ? <em className="slot-inline">when?</em> : '')}</span>
              <div>
                {e.title ? <h4>{e.title}</h4> : <Slot field={`buildLog[${i}]`} hint="Another milestone, bug or breakthrough." className="slot-line" />}
                {e.body && <p>{e.body}</p>}
              </div>
            </li>
          ))}
        </ol>
      </div>
    </section>
  )
}

/* ---------- get started ---------- */
export function GetStarted() {
  const steps = [
    { n: '01', t: 'Get an ElevenLabs key', b: <>Set it once in PowerShell: <code>setx ELEVENLABS_API_KEY "your-key"</code>. Without it, the hands still work.</> },
    { n: '02', t: 'Run SWISH', b: <><a href={links.download || `${links.github}/releases`} target="_blank" rel="noreferrer">Download the latest release</a>, unzip it and run <code>SWISH.exe</code>. Or build it yourself: clone the repo and run <code>.\scripts\run.ps1</code>.</> },
    { n: '03', t: 'Calibrate and play', b: <>On first launch, click <b>Start calibrating</b> (or say "start") and trace the moving ball with your palm. Then pick a game on the Controls screen, or just say "rocket league mode".</> },
  ]
  return (
    <section id="get" className="section">
      <Head eyebrow="get it" title="Three steps" accent="to hands-free." />
      <div className="get">
        <div className="reqs" data-reveal>
          <h3>You need</h3>
          <ul>
            <li><b>Windows</b><span>10 or 11</span></li>
            <li><b>Any webcam</b><span>built-in is fine</span></li>
            <li><b>A microphone</b><span>headset works best</span></li>
            <li><b>ElevenLabs API key</b><span>for voice only</span></li>
            <li><b>.NET 10 SDK</b><span>to build from source</span></li>
            <li><b>DirectX 12 GPU</b><span>optional, CPU works</span></li>
          </ul>
          <div className="get-ctas">
            {links.download ? (
              <a className="btn btn-solid" href={links.download}>Download SWISH ↓</a>
            ) : (
              <Slot field="links.download" hint="A release .zip, once there is one." className="slot-line" />
            )}
            <a className="btn btn-ghost" href={links.github} target="_blank" rel="noreferrer">Source on GitHub ↗</a>
          </div>
        </div>
        <ol className="steps">
          {steps.map((s, i) => (
            <li key={s.n} data-reveal style={v({ '--delay': `${i * 110}ms` })}>
              <span className="step-n">{s.n}</span>
              <div>
                <h4>{s.t}</h4>
                <p>{s.b}</p>
              </div>
            </li>
          ))}
        </ol>
      </div>
    </section>
  )
}

/* ---------- team ---------- */
const initials = (n: string) => n.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase()

export function Team() {
  return (
    <section id="team" className="section section-alt">
      <Head eyebrow="the humans" title="Built by" accent="these people." />
      <div className="team">
        {team.map((m, i) =>
          m.name ? (
            <article className="member" key={i} data-reveal style={v({ '--delay': `${i * 80}ms` })}>
              <div className="avatar">{m.photo ? <img src={m.photo} alt="" /> : <span>{initials(m.name)}</span>}</div>
              <h3>{m.name}</h3>
              {m.role && <p className="member-role">{m.role}</p>}
              {m.link && <a href={m.link} target="_blank" rel="noreferrer">profile ↗</a>}
            </article>
          ) : (
            SHOW_EMPTY_SLOTS && (
              <article className="member member-empty" key={i} data-reveal style={v({ '--delay': `${i * 80}ms` })}>
                <div className="avatar"><span>?</span></div>
                <Slot field={`team[${i}]`} hint="name, role, link, photo" className="slot-line" />
              </article>
            )
          ),
        )}
      </div>
    </section>
  )
}

/* ---------- FAQ ---------- */
const FAQ = [
  { q: 'Does it work with any game?', a: 'Anything that takes keyboard and mouse clicks at least. Certain Anti-cheats like Vanguard by Riot Games do not allow for the type of information that is passed by these types of programs, but besides that, SWISH presses keys with hardware scan codes, which is what games read. A game without a preset still gets the hand mouse, the finger keyboard, Desktop mode, and any commands you add in the command editor.' },
  { q: 'Do I need a special camera?', a: 'No. Any webcam works; the hand models run on the regular camera image. Good lighting and your hands fully in frame help the most.' },
  { q: 'Is my camera feed uploaded anywhere?', a: 'No. Hand tracking runs locally on your PC. Only microphone audio leaves your machine, streamed to ElevenLabs for transcription. Pause turns the camera and microphone off completely.' },
  { q: 'Do I need a powerful GPU?', a: 'No. The hand models run on the CPU by default. In Settings you can move them to any DirectX 12 GPU (NVIDIA, AMD or Intel) through DirectML; if that fails, SWISH falls back to the CPU.' },
  { q: 'Does it need the internet?', a: 'Only for voice. The hand mouse and finger keyboard work offline, and SWISH keeps running if voice can\'t connect.' },
  { q: 'Will it press keys when I\'m just talking?', a: 'No. A transcript only fires if every word belongs to a command (modifiers like "hard" or "little" included) or is a filler word like "then", so normal conversation is ignored. The exception is on purpose: "type …" types whatever follows. Say "stop listening" to pause voice.' },
  { q: 'Mac or Linux?', a: 'Not yet. The app is WPF and sends input through the Windows API.' },
]

export function Faq() {
  const [open, setOpen] = useState(0)
  return (
    <section id="faq" className="section">
      <Head eyebrow="faq" title="Questions," accent="answered." />
      <div className="faq" data-reveal>
        {FAQ.map((f, i) => (
          <div className={`faq-item ${open === i ? 'open' : ''}`} key={f.q}>
            <button aria-expanded={open === i} onClick={() => setOpen(open === i ? -1 : i)}>
              <span>{f.q}</span>
              <i aria-hidden="true" />
            </button>
            <div className="faq-a"><div><p>{f.a}</p></div></div>
          </div>
        ))}
      </div>
    </section>
  )
}
