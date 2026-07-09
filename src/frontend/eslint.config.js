import js from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist"] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ["**/*.{ts,tsx}"],
    languageOptions: { ecmaVersion: 2022, globals: globals.browser },
    plugins: { "react-hooks": reactHooks, "react-refresh": reactRefresh },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // Unterstrich-Praefix markiert bewusst ungenutzte Parameter/Variablen (z.B. Kategorie-Stubs).
      "@typescript-eslint/no-unused-vars": ["error", {
        args: "after-used", argsIgnorePattern: "^_",
        varsIgnorePattern: "^_", destructuredArrayIgnorePattern: "^_",
      }],
    },
  },
);
