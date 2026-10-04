import { useEffect, useRef, useState } from 'react'

/** Feeds --mx/--my (-0.5..0.5) to an element, eased toward the pointer like the hand mouse's glide. */
export function useDrift<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  useEffect(() => {
    const el = ref.current
    if (!el || matchMedia('(prefers-reduced-motion: reduce)').matches) return
    let raf = 0
    let tx = 0, ty = 0, x = 0, y = 0
    const onMove = (e: PointerEvent) => {
      const r = el.getBoundingClientRect()
      tx = (e.clientX - r.left) / r.width - 0.5
      ty = (e.clientY - r.top) / r.height - 0.5
    }
    const tick = () => {
      x += (tx - x) * 0.06
      y += (ty - y) * 0.06
      el.style.setProperty('--mx', x.toFixed(4))
      el.style.setProperty('--my', y.toFixed(4))
      raf = requestAnimationFrame(tick)
    }
    el.addEventListener('pointermove', onMove)
    raf = requestAnimationFrame(tick)
    return () => {
      el.removeEventListener('pointermove', onMove)
      cancelAnimationFrame(raf)
    }
  }, [])
  return ref
}

/** Wide art on a tall phone screen: stretch it to fill instead of cropping to the empty middle. */
export function usePortrait() {
  const [portrait, setPortrait] = useState(() => matchMedia('(max-aspect-ratio: 1/1)').matches)
  useEffect(() => {
    const mq = matchMedia('(max-aspect-ratio: 1/1)')
    const on = () => setPortrait(mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])
  return portrait
}
