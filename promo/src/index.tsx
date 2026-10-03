import React from 'react';
import {Composition, registerRoot} from 'remotion';
import {Film, DURATION, FPS} from './Film';
import './style.css';

const Root = () => <Composition id="Novaina" component={Film} durationInFrames={DURATION} fps={FPS} width={1920} height={1080}/>;
registerRoot(Root);
