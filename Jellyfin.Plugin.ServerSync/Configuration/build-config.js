#!/usr/bin/env node
/**
 * Build script for ServerSync Configuration Page
 *
 * Assembles configPage.html from component files:
 * - configPage.template.html (base HTML structure)
 * - shared.css, shared.js
 * - SourceServer/*.css, *.js
 * - ContentConfig/*.css, *.js
 * - SyncTable/*.css, *.js
 * - PaginatedTable/*.css, *.js
 * - main.js (orchestrator)
 *
 * Usage: node build-config.js
 */

const fs = require('fs');
const path = require('path');

const CONFIG_DIR = __dirname;

// File load order (dependencies must come first)
const CSS_FILES = [
    'shared.css',
    'SourceServer/sourceServer.css',
    'ContentConfig/contentConfig.css',
    'SyncTable/syncTable.css',
    'PaginatedTable/paginatedTable.css'
];

const JS_FILES = [
    'shared.js',
    'SourceServer/sourceServer.js',
    'ContentConfig/contentConfig.js',
    'PaginatedTable/paginatedTable.js',
    'SyncTable/syncTable.js',
    'main.js'
];

function readFile(relativePath) {
    const fullPath = path.join(CONFIG_DIR, relativePath);
    if (!fs.existsSync(fullPath)) {
        console.error(`ERROR: File not found: ${fullPath}`);
        process.exit(1);
    }
    return fs.readFileSync(fullPath, 'utf8');
}

function buildStyles() {
    let styles = '';
    for (const file of CSS_FILES) {
        const content = readFile(file);
        const name = file.replace(/\//g, '/');
        styles += `    <!-- Styles: ${name} -->\n`;
        styles += `    <style>\n`;
        // Indent CSS content
        const indented = content.split('\n').map(line => '        ' + line).join('\n');
        styles += indented + '\n';
        styles += `    </style>\n\n`;
    }
    return styles.trim();
}

function buildScripts() {
    let scripts = '';
    for (const file of JS_FILES) {
        const content = readFile(file);
        const name = file.replace(/\//g, '/');
        scripts += `    <!-- Scripts: ${name} -->\n`;
        scripts += `    <script type="text/javascript">\n`;
        // Indent JS content
        const indented = content.split('\n').map(line => '        ' + line).join('\n');
        scripts += indented + '\n';
        scripts += `    </script>\n\n`;
    }
    return scripts.trim();
}

function build() {
    console.log('Building configPage.html...\n');

    // Read template
    const template = readFile('configPage.template.html');

    // Build styles and scripts
    const styles = buildStyles();
    const scripts = buildScripts();

    // Replace placeholders
    let output = template;
    output = output.replace('<!-- {{STYLES}} -->', styles);
    output = output.replace('<!-- {{SCRIPTS}} -->', scripts);

    // Write output
    const outputPath = path.join(CONFIG_DIR, 'configPage.html');
    fs.writeFileSync(outputPath, output, 'utf8');

    console.log('CSS files included:');
    CSS_FILES.forEach(f => console.log(`  - ${f}`));
    console.log('\nJS files included:');
    JS_FILES.forEach(f => console.log(`  - ${f}`));
    console.log(`\nOutput: ${outputPath}`);
    console.log('Done!');
}

build();
