// ─────────────────────────────────────────────────────────────────────────────
//  Everything you fill in lives here. Empty strings show up on the page as dashed
//  "FILL IN" boxes that name the field to edit. When you're done (or want to hide
//  whatever's still empty), set SHOW_EMPTY_SLOTS to false.
//
//  Images and videos: drop files in landing/public/ and reference them as '/name.png'.
// ─────────────────────────────────────────────────────────────────────────────

export const SHOW_EMPTY_SLOTS = true

export const links = {
  github: 'https://github.com/KoolAidKrish/SWISH-Stormhacks2026',
  devpost: '', // e.g. 'https://devpost.com/software/swish'
  download: '', // e.g. a GitHub release .zip
}

export const demoVideo = {
  youtubeId: 'YHbdlz6OTAY', // the part after watch?v= ... takes priority over mp4
  mp4: '', // or a file in public/, e.g. '/demo.mp4'
  poster: '', // optional still shown before the mp4 plays, e.g. '/demo-poster.jpg'
  caption: '', // one line under the video, e.g. 'Rocket League, no controller, first try.'
}

// Value + label. Leave value empty for a slot. Only put numbers you've actually measured.
export const stats = [
  { value: '2', label: 'hands tracked at once, each with its own job' },
  { value: '5', label: 'voice presets: Desktop, Minecraft, Aimlabs, Rocket League, Bloons TD 6' },
  { value: '16%', label: 'of one core with no hand in view, down from 63% (console app, measured)' },
  { value: '~0.1 s', label: 'from releasing push-to-talk to the transcript (measured; push-to-talk is optional)' },
]

export const story = {
  // Why you built it, in your own words. 2-4 sentences.
  why: '',
  // Optional pull quote (from a tester, a teammate, a judge...) and who said it.
  quote: '',
  quoteBy: '',
}

// Screenshots / photos from the weekend. Put files in public/ and set src.
export const gallery = [
  { src: '', caption: 'The Controls screen: your games, live status, and what each hand does' },
  { src: '', caption: 'Trace-the-ball calibration' },
  { src: '', caption: 'The team at StormHacks' },
]

// How the weekend went. `when` is free text ('Sat 11:00', 'Hour 6', ...); leave it empty for a slot.
export const buildLog = [
  { when: '', title: 'Two prototypes', body: 'Hand tracking that moves the mouse, and a separate voice-to-keys app on ElevenLabs realtime speech-to-text. Both worked alone as console apps.' },
  { when: '', title: 'One app, one tray icon', body: 'Both engines moved into a single WPF app: the camera on one thread, voice async beside it. If one fails to start, the other keeps running.' },
  { when: '', title: '8 busy cores → a fraction of one', body: 'ONNX Runtime\'s worker threads spin-waited between frames, keeping about 8 cores busy at 30 fps. Letting them sleep kept the same inference speed for a fraction of the CPU.' },
  { when: '', title: 'Both hands, two jobs', body: 'The camera loop splits hands: right goes to the mouse, left goes to the finger keyboard, so you can steer and hold W at the same time.' },
  { when: '', title: 'The 50-keyterm wall', body: 'ElevenLabs takes at most 50 keyterms per session, so SWISH sends distinct words instead of whole phrases, with "stop listening" and preset switches first so they never get cut.' },
  { when: '', title: 'Idle throttle', body: 'With no hand in view for 1.5 s, the hand models run on every 6th frame. Idle CPU dropped from 63% to 16% of a core in the console app.' },
  { when: '', title: 'Your own gestures', body: 'Record a pose once and SWISH recognises it from other angles and distances: landmarks are compared relative to the wrist and palm size, and a gesture fires after 4 steady frames.' },
  { when: '', title: 'A real app', body: 'A full redesign: guided ball-tracing calibration, a Controls screen for each game, a command editor, and Settings for camera, mic, GPU and voice.' },
  { when: '', title: '', body: '' },
]

// Thanks / credits line in the footer, e.g. 'Thanks to the StormHacks organizers and mentors.'
export const thanks = ''
