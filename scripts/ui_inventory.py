"""Inventory interactive XAML surfaces for native acceptance; never infers a pass."""
from pathlib import Path
import argparse
import json
import xml.etree.ElementTree as ET

CONTROLS = {'Button', 'ToggleButton', 'CheckBox', 'RadioButton', 'TextBox', 'NumericUpDown',
            'Slider', 'ComboBox', 'ListBox', 'TreeView', 'TabControl', 'MenuItem', 'DataGrid',
            'ChartOverlay', 'ColorPicker', 'ScrollViewer'}
EVENTS = {'Click', 'Tapped', 'DoubleTapped', 'PointerPressed', 'PointerMoved', 'PointerWheelChanged',
          'KeyDown', 'SelectionChanged', 'TextChanged', 'ValueChanged'}


def inventory(root):
    rows = []
    for path in sorted((root / 'app/ColorCorrect.App').rglob('*.axaml')):
        if {'obj', 'bin'} & set(path.parts):
            continue
        document = ET.parse(path)
        counts = {}
        for element in document.iter():
            tag = element.tag.split('}')[-1]
            attributes = {key.split('}')[-1]: value for key, value in element.attrib.items()}
            if tag not in CONTROLS and not (EVENTS & set(attributes)):
                continue
            counts[tag] = counts.get(tag, 0) + 1
            identity = attributes.get('Name') or f'{tag}[{counts[tag]}]'
            rows.append({'id': f'{path.relative_to(root).as_posix()}#{identity}', 'control': tag,
                'label': attributes.get('AutomationProperties.Name') or attributes.get('Content') or attributes.get('Header') or attributes.get('Watermark'),
                'bindings_and_events': {key: value for key, value in attributes.items()
                    if key in EVENTS or key in {'Command', 'IsVisible', 'IsEnabled', 'IsChecked', 'ItemsSource', 'SelectedItem', 'Text', 'Value'}},
                'windows_sandbox': 'pending', 'linux_wsl': 'pending'})
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    rows = inventory(args.root.resolve())
    args.output.write_text(json.dumps({'scope': 'Interactive XAML inventory; visual/native validation recorded separately',
                                      'controls': rows}, indent=2), encoding='utf8')
    print(f'{len(rows)} interactive surfaces inventoried')


if __name__ == '__main__':
    main()
