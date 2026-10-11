// Lambda entry point. @aws-sdk/client-gamelift is provided by the nodejs22.x runtime,
// so the deployment zip contains only src/.
import { GameLiftClient } from '@aws-sdk/client-gamelift';
import { createApp } from './app.mjs';

export const handler = createApp({ client: new GameLiftClient({}) });
