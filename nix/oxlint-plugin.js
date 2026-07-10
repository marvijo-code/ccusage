const noServerActions = {
	create(context) {
		return {
			ExpressionStatement(node) {
				if (node.directive === 'use server') {
					context.report({
						message:
							'Server Actions are not allowed. Remove the "use server" directive.',
						node,
					});
				}
			},
		};
	},
};

const plugin = {
	meta: {
		name: 'ccusage',
	},
	rules: {
		'no-server-actions': noServerActions,
	},
};

export default plugin;
