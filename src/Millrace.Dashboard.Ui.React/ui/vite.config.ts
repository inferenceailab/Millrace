import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  // Relative asset URLs: the bundle is served from whatever prefix the consumer mounts the
  // dashboard at, which is not known at build time.
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // One JS and one CSS file keeps the embedded resource set small and the serving logic trivial.
    rollupOptions: {
      output: {
        entryFileNames: 'assets/app.js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
})
