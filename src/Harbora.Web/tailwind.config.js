/** @type {import('tailwindcss').Config} */
export default {
  darkMode: 'class',
  // Scan Razor views AND Vue/TS islands so no utility class is purged by mistake.
  content: [
    './Views/**/*.cshtml',
    './Scripts/**/*.{vue,ts,js}',
  ],
  theme: {
    extend: {
      colors: {
        // The whole UI is built on the slate scale. We back it with CSS variables so the same
        // markup works in dark and light: dark uses the real slate ramp, light uses an inverted
        // ramp (high numbers = light surfaces, low numbers = dark text). No per-view changes needed.
        slate: {
          50:  'rgb(var(--s50) / <alpha-value>)',
          100: 'rgb(var(--s100) / <alpha-value>)',
          200: 'rgb(var(--s200) / <alpha-value>)',
          300: 'rgb(var(--s300) / <alpha-value>)',
          400: 'rgb(var(--s400) / <alpha-value>)',
          500: 'rgb(var(--s500) / <alpha-value>)',
          600: 'rgb(var(--s600) / <alpha-value>)',
          700: 'rgb(var(--s700) / <alpha-value>)',
          800: 'rgb(var(--s800) / <alpha-value>)',
          900: 'rgb(var(--s900) / <alpha-value>)',
          950: 'rgb(var(--s950) / <alpha-value>)',
        },
        // Harbora's own brand ramp (a deep indigo → violet) — deliberately not a stock palette.
        brand: {
          300: '#a5b4fc',
          400: '#818cf8',
          500: '#6366f1',
          600: '#4f46e5',
          700: '#4338ca',
        },

        // Semantic colours. New markup uses these; the slate ramp above stays for existing views.
        // Named `line` and `ink` rather than `border` and `text` because Tailwind already owns
        // those utility prefixes, and colliding produces `border-border`.
        canvas:        'rgb(var(--canvas) / <alpha-value>)',
        surface:       'rgb(var(--surface) / <alpha-value>)',
        'surface-2':   'rgb(var(--surface-2) / <alpha-value>)',
        line:          'rgb(var(--border) / <alpha-value>)',
        'line-strong': 'rgb(var(--border-strong) / <alpha-value>)',
        ink:           'rgb(var(--text) / <alpha-value>)',
        'ink-muted':   'rgb(var(--text-muted) / <alpha-value>)',
        'ink-faint':   'rgb(var(--text-faint) / <alpha-value>)',
        accent:        'rgb(var(--brand) / <alpha-value>)',
        'accent-hover':'rgb(var(--brand-hover) / <alpha-value>)',
        'accent-text': 'rgb(var(--brand-text) / <alpha-value>)',
        'accent-soft': 'rgb(var(--brand-soft) / <alpha-value>)',

        ok:            'rgb(var(--ok) / <alpha-value>)',
        'ok-soft':     'rgb(var(--ok-soft) / <alpha-value>)',
        warn:          'rgb(var(--warn) / <alpha-value>)',
        'warn-soft':   'rgb(var(--warn-soft) / <alpha-value>)',
        danger:        'rgb(var(--error) / <alpha-value>)',
        'danger-soft': 'rgb(var(--error-soft) / <alpha-value>)',
        info:          'rgb(var(--info) / <alpha-value>)',
        'info-soft':   'rgb(var(--info-soft) / <alpha-value>)',
        idle:          'rgb(var(--idle) / <alpha-value>)',
        'idle-soft':   'rgb(var(--idle-soft) / <alpha-value>)',

        code:          'rgb(var(--code) / <alpha-value>)',
        'code-ink':    'rgb(var(--code-ink) / <alpha-value>)',
        terminal:      'rgb(var(--terminal) / <alpha-value>)',
        'terminal-ink':'rgb(var(--terminal-ink) / <alpha-value>)',
      },

      boxShadow: {
        panel: '0 1px 2px rgb(16 12 40 / 0.04)',
        'panel-hover': '0 4px 12px rgb(16 12 40 / 0.06)',
      },
      fontFamily: {
        sans: ['Inter', 'Vazirmatn', 'system-ui', 'sans-serif'],
      },
    },
  },
  plugins: [],
};
