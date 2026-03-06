import defaultTheme from 'tailwindcss/defaultTheme';

/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Inter', ...defaultTheme.fontFamily.sans],
      },
      keyframes: {
        fadeIn:    { from: { opacity: '0' }, to: { opacity: '1' } },
        fadeOut:   { from: { opacity: '1' }, to: { opacity: '0' } },
        slideUp:   { from: { opacity: '0', transform: 'translateY(16px) scale(0.98)' }, to: { opacity: '1', transform: 'translateY(0) scale(1)' } },
        slideDown: { from: { opacity: '1', transform: 'translateY(0) scale(1)' }, to: { opacity: '0', transform: 'translateY(16px) scale(0.98)' } },
        shimmer:   { '0%': { backgroundPosition: '-1000px 0' }, '100%': { backgroundPosition: '1000px 0' } },
        glow: {
          '0%, 100%': { boxShadow: '0 0 8px 0 rgba(99,102,241,0.3)' },
          '50%':      { boxShadow: '0 0 20px 4px rgba(99,102,241,0.45)' },
        },
        toastIn:  { from: { opacity: '0', transform: 'translateX(100%)' }, to: { opacity: '1', transform: 'translateX(0)' } },
        toastOut: { from: { opacity: '1', transform: 'translateX(0)' }, to: { opacity: '0', transform: 'translateX(100%)' } },
      },
      animation: {
        'fade-in':   'fadeIn 0.2s ease-out',
        'fade-out':  'fadeOut 0.15s ease-in forwards',
        'slide-up':  'slideUp 0.25s cubic-bezier(0.16,1,0.3,1)',
        'slide-down':'slideDown 0.2s ease-in forwards',
        'shimmer':   'shimmer 2s infinite linear',
        'glow':      'glow 1.5s ease-in-out 1',
        'toast-in':  'toastIn 0.3s ease-out',
        'toast-out': 'toastOut 0.25s ease-in forwards',
      },
    },
  },
  plugins: [],
}
