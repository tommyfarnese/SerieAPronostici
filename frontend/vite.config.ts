import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
export default defineConfig({
  plugins: [react()],
  base: process.env.GITHUB_PAGES === 'true' && process.env.VITE_USE_CUSTOM_DOMAIN !== 'true' ? '/SerieAPronostici/' : '/',
  server: { proxy: { '/api': 'http://localhost:5080' } },
})
