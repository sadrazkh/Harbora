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
      },
      fontFamily: {
        sans: ['Inter', 'Vazirmatn', 'system-ui', 'sans-serif'],
      },
    },
  },
  plugins: [],
};
