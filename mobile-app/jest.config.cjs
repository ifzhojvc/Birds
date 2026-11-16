module.exports = {
  rootDir: "..",
  preset: require.resolve("jest-expo/jest-preset"),
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
  coverageDirectory: "<rootDir>/mobile-app/coverage",
  coverageThreshold: {
    global: { statements: 90, branches: 90, functions: 90, lines: 90 },
  },
  coverageReporters: ["text-summary", "html", "json", "json-summary", "lcov"],
};
