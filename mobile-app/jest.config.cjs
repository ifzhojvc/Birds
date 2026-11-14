module.exports = {
  rootDir: "..",
  preset: "<rootDir>/mobile-app/node_modules/jest-expo",
  roots: ["<rootDir>/mobile-app", "<rootDir>/shared"],
  moduleDirectories: ["node_modules", "<rootDir>/mobile-app/node_modules"],
  cacheDirectory: "<rootDir>/mobile-app/.cache/jest",
  testMatch: ["<rootDir>/mobile-app/tests/**/*.test.ts?(x)"],
  setupFilesAfterEnv: ["<rootDir>/mobile-app/tests/setup.cjs"],
  collectCoverageFrom: [
    "mobile-app/App.tsx",
    "mobile-app/index.ts",
    "mobile-app/src/**/*.{ts,tsx}",
    "shared/**/*.ts",
  ],
  coverageReporters: ["text-summary", "html", "json", "json-summary", "lcov"],
};
