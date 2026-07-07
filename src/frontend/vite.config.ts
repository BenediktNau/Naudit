import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": path.resolve(__dirname, "./src") },
  },
  server: {
    proxy: {
      // BFF: API und Auth-Flows laufen im Dev-Modus gegen das lokale Naudit (dotnet run, Port 5290).
      "/api": { target: "http://localhost:5290", changeOrigin: true },
      "/auth": { target: "http://localhost:5290", changeOrigin: true },
    },
  },
});
