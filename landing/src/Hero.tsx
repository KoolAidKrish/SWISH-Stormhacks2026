import { type CSSProperties } from 'react'
import { useDrift, usePortrait } from './useDrift'

export const TITLE = 'SWISH'
export const ACRONYM = ['SERIAL', 'WIRELESS', 'INTERACTIVE', 'SYSTEM', 'FOR', 'HUMANS']

/** The title card, rebuilt in SVG so every shape can animate in and drift with the pointer. */
export default function Hero() {
  const ref = useDrift<HTMLElement>()
  const portrait = usePortrait()

  return (
    <header className="hero" ref={ref} id="top">
      <svg className="hero-art" viewBox="0 0 1024 582" preserveAspectRatio={portrait ? 'none' : 'xMidYMid slice'} aria-hidden="true">
        {/* top-left wedges */}
        <g className="px" style={{ '--d': 18 } as CSSProperties}>
          <g className="in-left" style={{ '--i': 0 } as CSSProperties}>
            <polygon className="brand" points="18,8 178,8 178,250 18,92" />
            <polygon className="brand" points="100,168 100,322 24,245" />
            <polygon className="brand" points="178,250 178,398 102,322" />
            <polygon className="brand" points="100,322 100,474 24,398" />
          </g>
        </g>

        {/* long light stripe, top */}
        <g className="px" style={{ '--d': 10 } as CSSProperties}>
          <polygon className="stripe in-diag" points="362,8 418,8 18,408 18,352" />
        </g>

        {/* bottom-left stripe */}
        <g className="px" style={{ '--d': 14 } as CSSProperties}>
          <polygon className="stripe in-diag" style={{ '--i': 3 } as CSSProperties} points="18,450 142,574 86,574 18,506" />
        </g>

        {/* thin frame, draws itself */}
        <g className="px" style={{ '--d': 4 } as CSSProperties}>
          <path className="frame" pathLength={1} d="M232 92 L264 58 H638 L726 8 H975 L1012 45 V118" />
          <path className="frame" pathLength={1} style={{ '--i': 1 } as CSSProperties} d="M232 92 V402 L354 524 H584" />
          <path className="hair" pathLength={1} d="M60 341 H302" />
          <path className="hair" pathLength={1} style={{ '--i': 1 } as CSSProperties} d="M735 341 H915" />
        </g>

        {/* heavy white L rail */}
        <g className="px" style={{ '--d': 7 } as CSSProperties}>
          <path className="rail" pathLength={1} d="M206 0 V430 L330 548 H660" />
        </g>

        {/* top-right X */}
        <g className="px" style={{ '--d': 24 } as CSSProperties}>
          <g className="x-mark">
            <rect className="brand" x="800" y="68" width="250" height="42" transform="rotate(45 925 89)" />
            <rect className="brand" x="800" y="68" width="250" height="42" transform="rotate(-45 925 89)" />
          </g>
        </g>

        {/* bottom-right block with cut-outs */}
        <g className="px" style={{ '--d': 30 } as CSSProperties}>
          <g className="in-right">
            <polygon className="rail-fill" points="566,582 1020,128 1020,150 588,582" />
            <polygon className="rail-fill" points="604,582 1020,166 1020,582" />
            <polygon className="brand" points="784,412 916,282 916,544" />
            <polygon className="brand" points="996,316 996,472 918,394" />
            <polygon className="brand" points="996,472 996,582 918,582 918,550" />
          </g>
        </g>

        {/* one-time scan sweep */}
        <rect className="scan" x="0" y="0" width="1024" height="3" />
      </svg>

      <div className="hero-copy">
        <p className="eyebrow hero-eyebrow">StormHacks 2026 · SFU</p>
        <h1 className="hero-title" aria-label={TITLE}>
          {TITLE.split('').map((c, i) => (
            <span key={i} className="hero-letter" style={{ '--i': i } as CSSProperties} aria-hidden="true">{c}</span>
          ))}
        </h1>
        <p className="hero-acronym" aria-label="Serial Wireless Interactive System for Humans">
          {ACRONYM.map((w, i) => (
            <span key={w} style={{ '--i': i } as CSSProperties} aria-hidden="true">
              {w}{i < ACRONYM.length - 1 && <b> · </b>}
            </span>
          ))}
        </p>
        <p className="hero-tagline">A touchless control system<br />for your gaming experience</p>
        <div className="hero-ctas">
          <a className="btn btn-solid" href="#demo">Try a voice command</a>
          <a className="btn btn-ghost" href="https://github.com/KoolAidKrish/SWISH-Stormhacks2026" target="_blank" rel="noreferrer">View on GitHub ↗</a>
        </div>
      </div>

      <a className="scroll-cue" href="#how" aria-label="Scroll to how it works">
        <span>SCROLL</span>
        <i />
      </a>
    </header>
  )
}
