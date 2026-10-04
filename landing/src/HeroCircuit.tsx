import { type CSSProperties } from 'react'
import { ACRONYM, TITLE } from './Hero'
import { useDrift, usePortrait } from './useDrift'

const v = (o: Record<string, string | number>) => o as CSSProperties
const TRACE = 'M18 528 H512 L558 482 H706 L752 528 H842 L888 482 V330 L918 300 H1010'

/** v2 "Circuit" title card: left-aligned title, traces along the bottom, the tagline in a chip. */
export default function HeroCircuit() {
  const ref = useDrift<HTMLElement>()
  const portrait = usePortrait()

  return (
    <header className="hero hero-circuit" ref={ref} id="top">
      <svg className="hero-art" viewBox="0 0 1024 582" preserveAspectRatio={portrait ? 'none' : 'xMidYMid slice'} aria-hidden="true">
        {/* top-left triangles drop in one after another */}
        <g className="px" style={v({ '--d': 16 })}>
          <polygon className="brand in-down" style={v({ '--i': 0 })} points="18,8 205,8 18,196" />
          <polygon className="brand in-down" style={v({ '--i': 1 })} points="205,8 308,8 205,114" />
          <polygon className="brand in-down" style={v({ '--i': 2 })} points="310,8 412,8 310,114" />
          <polygon className="pinkf pop" points="102,114 205,114 102,214" />
        </g>

        {/* thin pink line along the top */}
        <g className="px" style={v({ '--d': 5 })}>
          <path className="frame" pathLength={1} d="M102 114 H590 L662 50 H836 L876 10" />
        </g>

        {/* X and its long arm */}
        <g className="px" style={v({ '--d': 22 })}>
          <g className="in-corner">
            <path className="bar" d="M640 0 L1010 370" />
            <path className="bar" d="M752 0 L640 118" />
            <path className="bar thin" d="M943 0 V268" />
          </g>
        </g>

        {/* white hairline */}
        <g className="px" style={v({ '--d': 4 })}>
          <path className="hair-w" pathLength={1} d="M18 482 H482 L546 420 H1010" />
        </g>

        {/* circuit trace with a signal running along it */}
        <g className="px" style={v({ '--d': 9 })}>
          <path className="trace" pathLength={1} d={TRACE} />
          <path className="signal" pathLength={1} d={TRACE} />
          <path className="signal" pathLength={1} d={TRACE} style={v({ '--i': 1 })} />
        </g>

        {/* big pink corner */}
        <g className="px" style={v({ '--d': 28 })}>
          <polygon className="pinkf in-right" points="752,582 1010,358 1010,582" />
        </g>

        <rect className="scan" x="0" y="0" width="1024" height="3" />
      </svg>

      <div className="circuit-copy">
        <div className="circuit-title">
          <p className="eyebrow hero-eyebrow">StormHacks 2026 · SFU</p>
          <h1 className="hero-title" aria-label={TITLE}>
            {TITLE.split('').map((c, i) => (
              <span key={i} className="hero-letter" style={v({ '--i': i })} aria-hidden="true">{c}</span>
            ))}
          </h1>
          <p className="hero-acronym" aria-label="Serial Wireless Interactive System for Humans">
            {ACRONYM.map((w, i) => (
              <span key={w} style={v({ '--i': i })} aria-hidden="true">
                {w}{i < ACRONYM.length - 1 && <b> · </b>}
              </span>
            ))}
          </p>
        </div>
        <div className="circuit-side">
          <p className="tag-chip">A touchless control system<br />for your gaming experience.</p>
          <div className="hero-ctas">
            <a className="btn btn-solid" href="#left-hand">See both hands</a>
            <a className="btn btn-ghost" href="#demo">Try voice</a>
          </div>
        </div>
      </div>

      <a className="scroll-cue" href="#how" aria-label="Scroll to how it works">
        <span>SCROLL</span>
        <i />
      </a>
    </header>
  )
}
