import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` serves the SPA on :5173 and proxies the API to the .NET server.
// `npm run build` writes dist/, which the .NET server serves on :5317.
// Two pages: the dashboard, and print.html (one resume, styled by a theme, printed to PDF).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5317',
      '/themes': 'http://localhost:5317',
    },
  },
  build: {
    rollupOptions: {
      input: { main: 'index.html', print: 'print.html' },
    },
  },
});
