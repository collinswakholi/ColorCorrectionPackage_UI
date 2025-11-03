import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react({
    // Fast Refresh optimization
    fastRefresh: true,
    // Babel optimization for production
    babel: {
      compact: true,
    }
  })],
  
  // Dev server with API proxy
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5000'
    },
    // Enable HMR (Hot Module Replacement) optimizations
    hmr: true,
    // Faster file watching
    watch: {
      usePolling: false,
    }
  },
  
  // Build optimizations
  build: {
    outDir: 'dist',
    // Enable chunk splitting for better caching
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom'],
        }
      }
    },
    // Increase chunk size warning limit (base64 images can be large)
    chunkSizeWarningLimit: 1000,
    // Optimize minification
    minify: 'terser',
    terserOptions: {
      compress: {
        drop_console: true, // Remove console.logs in production
        drop_debugger: true,
        pure_funcs: ['console.log', 'console.info']
      }
    }
  },
  
  // Dependency optimization
  optimizeDeps: {
    include: ['react', 'react-dom'],
    // Force re-optimization on config change
    force: false
  }
})
