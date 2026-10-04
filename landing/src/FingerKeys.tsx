import { useEffect, useRef, useState } from 'react'

// Mirrors src/HandGestureRecognition/HandKeyboard.cs: fold a finger to hold its key,
// a fist (4+ fingers down) is the rest pose, and every key lets go when the hand leaves view.
const FINGERS = [
  { name: 'Thumb', key: 'Space', code: 'Space', joints: [1, 2, 3, 4] },
  { name: 'Index', key: 'D', code: 'KeyD', joints: [5, 6, 7, 8] },
  { name: 'Middle', key: 'W', code: 'KeyW', joints: [9, 10, 11, 12] },
  { name: 'Ring', key: 'A', code: 'KeyA', joints: [13, 14, 15, 16] },
  { name: 'Pinky', key: 'S', code: 'KeyS', joints: [17, 18, 19, 20] },
] as const

// Open left hand (palm to camera), 200×220 box
const OPEN: [number, number][] = [
  [100, 200], [130, 180], [150, 156], [162, 132], [172, 110],
  [120, 114], [124, 82], [126, 60], [127, 40],
  [99, 108], [99, 72], [99, 48], [99, 26],
  [79, 112], [75, 80], [73, 58], [71, 40],
  [61, 124], [52, 100], [47, 84], [43, 68],
]
const BONES = [
  [0, 1], [1, 2], [2, 3], [3, 4], [0, 5], [5, 6], [6, 7], [7, 8], [5, 9], [9, 10], [10, 11], [11, 12],
  [9, 13], [13, 14], [14, 15], [15, 16], [13, 17], [17, 18], [18, 19], [19, 20], [0, 17],
]
const PALM: [number, number] = [96, 138]
const lerp = (a: number, b: number, t: number) => a + (b - a) * t
const toward = (p: [number, number], q: [number, number], t: number): [number, number] => [lerp(p[0], q[0], t), lerp(p[1], q[1], t)]

/** Where each landmark sits when its finger is fully curled. */
const FOLDED: [number, number][] = (() => {
  const f = OPEN.map(p => [...p] as [number, number])
  for (const { joints: [m, p, d, t], name } of FINGERS) {
    if (name === 'Thumb') {
      f[p] = toward(OPEN[p], PALM, 0.25)
      f[d] = toward(OPEN[d], PALM, 0.55)
      f[t] = toward(OPEN[t], PALM, 0.8)
    } else {
      f[p] = toward(OPEN[m], OPEN[p], 0.5)
      f[d] = toward(f[p], PALM, 0.25)
      f[t] = toward(OPEN[m], PALM, 0.3)
    }
  }
  return f
})()

// What the hand does on its own until you touch it
const SCRIPT: { fold: number[]; ms: number }[] = [
  { fold: [], ms: 900 },
  { fold: [2], ms: 1100 },
  { fold: [2, 1], ms: 900 },
  { fold: [2], ms: 500 },
  { fold: [2, 0], ms: 300 },
  { fold: [2], ms: 700 },
  { fold: [3], ms: 900 },
  { fold: [4], ms: 800 },
  { fold: [], ms: 600 },
  { fold: [0, 1, 2, 3, 4], ms: 1400 },
]

export default function FingerKeys() {
  const [target, setTarget] = useState<boolean[]>([false, false, false, false, false])
  const [shown, setShown] = useState<number[]>([0, 0, 0, 0, 0])
  const [visible, setVisible] = useState(true)
  const [auto, setAuto] = useState(true)
  const autoRef = useRef(true)
  autoRef.current = auto
  const OPEN_HAND = [false, false, false, false, false]
  // first manual touch: drop whatever the autoplay was doing and start from an open hand
  const manual = (fn: (t: boolean[]) => boolean[]) => {
    const fromAuto = autoRef.current
    autoRef.current = false
    setAuto(false)
    setVisible(true)
    setTarget(t => fn(fromAuto ? OPEN_HAND : t))
  }
  const shownRef = useRef(shown)
  const box = useRef<HTMLDivElement>(null)

  // Ease the drawn fold toward the target
  useEffect(() => {
    let raf = 0
    const step = () => {
      const cur = shownRef.current
      const next = cur.map((c, i) => c + ((target[i] ? 1 : 0) - c) * 0.28)
      const done = next.every((n, i) => Math.abs(n - (target[i] ? 1 : 0)) < 0.01)
      shownRef.current = done ? target.map(t => (t ? 1 : 0)) : next
      setShown(shownRef.current)
      if (!done) raf = requestAnimationFrame(step)
    }
    raf = requestAnimationFrame(step)
    return () => cancelAnimationFrame(raf)
  }, [target])

  // Autoplay while on screen and untouched
  useEffect(() => {
    if (!auto) return
    const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches
    if (reduced) return
    let i = 0, timer = 0, onScreen = false
    const io = new IntersectionObserver(([e]) => {
      const was = onScreen
      onScreen = e.isIntersecting
      if (onScreen && !was) next()
      if (!onScreen) clearTimeout(timer)
    })
    const next = () => {
      const s = SCRIPT[i++ % SCRIPT.length]
      setTarget(FINGERS.map((_, k) => s.fold.includes(k)))
      timer = window.setTimeout(() => onScreen && next(), s.ms)
    }
    if (box.current) io.observe(box.current)
    return () => { io.disconnect(); clearTimeout(timer) }
  }, [auto])

  // Your real keyboard folds the fingers too: hold W and the middle finger curls
  useEffect(() => {
    const set = (code: string, down: boolean) => {
      const k = FINGERS.findIndex(f => f.code === code)
      if (k < 0 || !box.current) return false
      const r = box.current.getBoundingClientRect()
      if (r.bottom < 0 || r.top > innerHeight) return false
      if ((document.activeElement as HTMLElement)?.tagName === 'INPUT') return false
      manual(t => t.map((x, i) => (i === k ? down : x)))
      return true
    }
    const kd = (e: KeyboardEvent) => { if (!e.repeat && set(e.code, true)) e.preventDefault() }
    const ku = (e: KeyboardEvent) => { set(e.code, false) }
    addEventListener('keydown', kd)
    addEventListener('keyup', ku)
    return () => { removeEventListener('keydown', kd); removeEventListener('keyup', ku) }
  }, [])

  const toggle = (k: number) => manual(t => t.map((x, i) => (i === k ? !x : x)))

  const downCount = target.filter(Boolean).length
  const resting = downCount >= 4
  const held = visible && !resting ? FINGERS.filter((_, i) => target[i]).map(f => f.key) : []

  const pts = OPEN.map((p, i) => {
    const f = FINGERS.findIndex(fg => (fg.joints as readonly number[]).includes(i) && fg.joints[0] !== i)
    const t = f < 0 ? 0 : shown[f]
    return [lerp(p[0], FOLDED[i][0], t), lerp(p[1], FOLDED[i][1], t)] as [number, number]
  })

  let status = 'Open hand, nothing pressed'
  if (!visible) status = 'Hand out of view, every key released'
  else if (resting) status = 'Fist: rest pose, nothing pressed'
  else if (held.length) status = `Holding ${held.join(' + ')}`

  return (
    <div className="fk" ref={box} data-reveal>
      <div className={`fk-stage ${visible ? '' : 'gone'} ${resting ? 'rest' : ''}`}>
        <span className="fk-tag">LEFT HAND CAMERA</span>
        <svg viewBox="0 0 200 220" className="fk-hand" role="img" aria-label={status}>
          <g className="fk-skel">
            {BONES.map(([a, b], i) => (
              <line key={i} x1={pts[a][0]} y1={pts[a][1]} x2={pts[b][0]} y2={pts[b][1]} />
            ))}
            {pts.map(([x, y], i) => <circle key={i} cx={x} cy={y} r={i % 4 === 0 && i ? 5 : 3.6} />)}
          </g>
          {FINGERS.map((f, k) => {
            const tip = pts[f.joints[3]]
            return (
              <g key={f.name} className={`fk-hit ${target[k] && visible && !resting ? 'on' : ''}`} onClick={() => toggle(k)}>
                <polyline points={f.joints.map(j => pts[j].join(',')).join(' ')} />
                <text x={tip[0]} y={tip[1] - 10} textAnchor="middle" opacity={1 - shown[k]}>{f.key === 'Space' ? 'SPC' : f.key}</text>
              </g>
            )
          })}
        </svg>
        <p className="fk-status" aria-live="polite">{status}</p>
      </div>

      <div className="fk-side">
        <div className="fk-map">
          {FINGERS.map((f, k) => (
            <button key={f.name} className={`fk-row ${target[k] ? 'folded' : ''} ${held.includes(f.key) ? 'held' : ''}`} onClick={() => toggle(k)}>
              <span className="fk-finger">{f.name}</span>
              <span className="fk-dots" aria-hidden="true"><i /><i /><i /></span>
              <kbd>{f.key}</kbd>
            </button>
          ))}
        </div>
        <div className="fk-actions">
          <button className="chip" onClick={() => manual(() => [true, true, true, true, true])}>Make a fist</button>
          <button className="chip" onClick={() => manual(() => OPEN_HAND)}>Open hand</button>
          <button className="chip" onClick={() => { setAuto(false); setVisible(v => !v) }}>{visible ? 'Move hand out of view' : 'Bring hand back'}</button>
        </div>
        <p className="fk-hint">
          Click a finger, or hold <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> <kbd>Space</kbd> on your keyboard and watch the hand fold.
        </p>
      </div>
    </div>
  )
}
