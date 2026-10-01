import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` serves the SPA on :5173 and proxies the API to the .NET server.
// `npm run build` writes into the server's wwwroot so it serves everything from :5317.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5317' },
  },
  build: {
    outDir: '../Server/wwwroot',
    emptyOutDir: true,
  },
});
