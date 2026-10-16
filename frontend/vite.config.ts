import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
export default defineConfig({
  plugins: [react()],
  base: process.env.GITHUB_PAGES === 'true' ? '/SerieAPronostici/' : '/',
  server: { proxy: { '/api': 'http://localhost:5080' } },
})
