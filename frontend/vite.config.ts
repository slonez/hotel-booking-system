import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Forward API calls to the Gateway, so the browser sees one origin (no CORS in development).
    proxy: {
      '/api': 'http://localhost:5172',
    },
  },
})
