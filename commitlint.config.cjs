module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    // Keep this flexible; tighten later if you want a strict scope policy.
    'scope-empty': [2, 'never'],
    'scope-enum': [
      2,
      'always',
      [
        'helm',
        'ebay-adapter',
        'shopware',
        'ci',
        'infra',
        'docs',
        'deps',
        'security',
      ],
    ],
  },
};

