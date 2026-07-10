import assert from 'node:assert/strict';
import { it } from 'node:test';
import plugin from './oxlint-plugin.js';

function lintExpressionStatement(node) {
	const messages = [];
	const rule = plugin.rules['no-server-actions'];
	const visitor = rule.create({
		report({ message }) {
			messages.push(message);
		},
	});

	visitor.ExpressionStatement(node);

	return messages;
}

void it('rejects the use server directive', () => {
	assert.deepEqual(
		lintExpressionStatement({
			type: 'ExpressionStatement',
			directive: 'use server',
		}),
		['Server Actions are not allowed. Remove the "use server" directive.'],
	);
});

void it('allows the use client directive', () => {
	assert.deepEqual(
		lintExpressionStatement({
			type: 'ExpressionStatement',
			directive: 'use client',
		}),
		[],
	);
});

void it('allows a use server string outside a directive prologue', () => {
	assert.deepEqual(
		lintExpressionStatement({
			type: 'ExpressionStatement',
			expression: {
				type: 'Literal',
				value: 'use server',
			},
		}),
		[],
	);
});
