import { Pipe, PipeTransform } from '@angular/core';

@Pipe({
  name: 'filterByMac'
})
export class FilterByMacPipe implements PipeTransform {
  transform(devices: any[], search: string): any[] {
    if (!devices || !search) return devices;
    return devices.filter(d =>
      d.mac.toLowerCase().includes(search.toLowerCase())
    );
  }
}
