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
